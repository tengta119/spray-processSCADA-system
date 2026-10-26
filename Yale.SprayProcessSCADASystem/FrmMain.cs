using Microsoft.Extensions.DependencyInjection;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Yale.Helper;
using YR.FrameWork.DependencyInjection;
using MiniExcelLibs;
using Yale.Model;
using IoTClient.Common.Enums;
using IoTClient.Enums;
using Microsoft.Extensions.Logging;
using Sunny.UI.Win32;
using Yale.BLL;
using System.Timers;
using Microsoft.Win32;
using Yale.SprayProcessSCADASystem.Communication;

namespace Yale.SprayProcessSCADASystem
{
    public partial class FrmMain : UIHeaderAsideMainFooterFrame, ISingletonSelfDependency
    {
        #region 成员变量

        private CancellationTokenSource cts = new CancellationTokenSource();

        private readonly ILogger<FrmMain> _logger;
        private readonly UserManager _userManager;
        private readonly AuthManager _authManager;
        private readonly DataManager _dataManager;

        private bool dashboardUpdating;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        private Dictionary<string, Control> pageControls = new Dictionary<string, Control>
{
    { "控制模块", Globals.ServiceProvider.GetRequiredService<PageTotalEquipmentControl>() },
    { "用户模块", Globals.ServiceProvider.GetRequiredService<PageUserManage>() },
    { "权限模块", Globals.ServiceProvider.GetRequiredService<PageAuthManage>() },
    { "监控模块", Globals.ServiceProvider.GetRequiredService<PageEquipmentMonitor>() },
    { "监控模块1", Globals.ServiceProvider.GetRequiredService<PageEquipmentMonitor1>() },
    { "监控模块2", Globals.ServiceProvider.GetRequiredService<PageEquipmentMonitor2>() },
    { "配方模块", Globals.ServiceProvider.GetRequiredService<PageRecipeManage>() },
    { "日志模块", Globals.ServiceProvider.GetRequiredService<PageLogManage>() },
    { "报表模块", Globals.ServiceProvider.GetRequiredService<PageReportManage>() },
    { "图表模块", Globals.ServiceProvider.GetRequiredService<PageChartManage>() },
    { "参数模块", Globals.ServiceProvider.GetRequiredService<PageSystemParameterSet>() }
};

        private List<string> AlarmList = new List<string>();
        #endregion

        #region 初始化

        public FrmMain(ILogger<FrmMain> _logger, UserManager userManager, AuthManager authManager, DataManager dataManager)
        {
            InitializeComponent();

            this._logger = _logger;
            this._userManager = userManager;
            this._authManager = authManager;
            this._dataManager = dataManager;

            this.lbl_User.Text = "访客";
            //Init();


            this.Closed += (s, e) =>
            {
                timer.Stop();
                cts.Cancel();
                Globals.HasPlcData = false;
            };

        }
        public override void Init()
        {

            InitConfig();
            InitAsideUI();
            InitHeaderUI();
            IniPlcClient();
            IniOther();

        }

        private void IniOther()
        {
            Directory.CreateDirectory(Globals.DelFilePath);
            if (Globals.SaveDay != "不清理" &&
                int.TryParse(Globals.SaveDay.TrimEnd('天'), out var days) && days > 0)
            {
                DelFile.DeleteFolder(Globals.DelFilePath, days);
            }
            timer.Interval = 1000;
            timer.Tick += Timer_Elapsed;
            timer.Start();
        }

        // The WinForms timer updates controls on the UI thread; only PLC I/O runs in the background.
        private async void Timer_Elapsed(object? sender, EventArgs e)
        {
            this.lbl_Time.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (dashboardUpdating || !Globals.HasPlcData || Globals.PlcClient?.Connected != true)
                return;

            dashboardUpdating = true;
            try
            {
                string Value(string name) => Globals.DataDic.TryGetValue(name, out var value) ? value.ToString() ?? "0" : "0";
                this.lbl_Temperature.Text = Value(this.lbl_Temperature.TagString) + "℃";
                this.lbl_Humidness.Text = Value(this.lbl_Humidness.TagString) + "%";
                this.lbl_ProducteCount.Text = Value(this.lbl_ProducteCount.TagString);
                this.lbl_BadCount.Text = Value(this.lbl_BadCount.TagString);
                this.lbl_Beat.Text = Value(this.lbl_Beat.TagString);
                this.lbl_TotalAlarm.Text = Value(this.lbl_TotalAlarm.TagString);

                try
                {
                    var runtime = RuntimeStatusHelper.DataManager;
                    this.lbl_CPUInformation.Text = runtime.GetCpuUtilization() + "%";
                    var memory = runtime.GetMemoryStatus();
                    this.lbl_MemoryInformation.Text = memory.dwMemoryLoad + "%";
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取电脑运行状态失败");
                }

                var alarms = AlarmList.Where(name => Value(name) == "1").ToList();
                this.st_AlarmInfo.Text = alarms.Count == 0 ? "系统正常" : string.Join(",", alarms);
                this.st_AlarmInfo.ForeColor = alarms.Count == 0 ? Color.Green : Color.Red;
                this.led_ProducteState.On = true;
                this.led_ProducteState.Color = alarms.Count == 0 ? Color.Green : Color.Red;
                this.led_ProducteState.Blink = alarms.Count != 0;
                this.led_ProducteState.BlinkInterval = 500;

                var dto = new AddDataDto { InsertTime = DateTime.Now };
                foreach (var name in Globals.SaveList)
                {
                    var property = typeof(AddDataDto).GetProperty(name);
                    if (property?.PropertyType == typeof(string))
                        property.SetValue(dto, Value(name));
                }
                var result = await _dataManager.AddDataAsync(dto);
                if (result.Status == SystemEnums.Result.Fail)
                    _logger.LogError("保存数据失败: {Message}", result.Msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新实时数据失败");
            }
            finally
            {
                dashboardUpdating = false;
            }
        }

        private void InitConfig()
        {
            Globals.Protocol = Globals.IniFile.ReadString("PLC参数", "通信协议", "ModbusTcp");
            if (Globals.Protocol != "ModbusTcp" && Globals.Protocol != "SiemensS7")
                throw new InvalidOperationException("通信协议必须为 ModbusTcp 或 SiemensS7。");
            bool modbus = Globals.Protocol == "ModbusTcp";
            string table = Globals.IniFile.ReadString("PLC参数", "变量表地址",
                modbus ? "PLC_Modbus_Config.xlsx" : "PLC_Var_Config.xlsx");
            Globals.PlcVarConfigPath = Path.GetFullPath(table, Application.StartupPath);
            Globals.IpAddress = Globals.IniFile.ReadString("PLC参数", "PLC地址", "127.0.0.1");
            Globals.Port = Globals.IniFile.ReadInt("PLC参数", "PLC端口", modbus ? 502 : 102);
            if (Globals.Port < 1 || Globals.Port > 65535)
                throw new InvalidOperationException("PLC端口必须在1至65535之间。");
            var stationId = Globals.IniFile.ReadInt("PLC参数", "站号", 1);
            if (stationId < 1 || stationId > 247)
                throw new InvalidOperationException("Modbus站号必须在1至247之间。");
            Globals.StationId = (byte)stationId;
            Globals.ModbusWordOrder = Globals.IniFile.ReadString("PLC参数", "浮点字序", "ABCD");
            Globals.CpuType = Enum.Parse<SiemensVersion>(Globals.IniFile.ReadString("PLC参数", "CPU类型", "S7_1200"));
            Globals.Rack = Globals.IniFile.ReadByte("PLC参数", "机架号", 0);
            Globals.Slot = Globals.IniFile.ReadByte("PLC参数", "插槽号", 0);
            Globals.ConnectTimeOut = Globals.IniFile.ReadInt("PLC参数", "超时时间", 3000);
            Globals.ReadTimeInterval = Globals.IniFile.ReadInt("PLC参数", "循环读取时间", 500);
            Globals.ReConnectTimeInterval = Globals.IniFile.ReadInt("PLC参数", "重连时间", 3000);
            if (Globals.ConnectTimeOut <= 0 || Globals.ReadTimeInterval <= 0 || Globals.ReConnectTimeInterval <= 0)
                throw new InvalidOperationException("通信超时和采集/重连间隔必须大于0。");
            Globals.DelFilePath = Path.GetFullPath(Globals.IniFile.ReadString("系统参数", "删除文件夹路径", "Logs"), Application.StartupPath);
            Globals.SaveDay = Globals.IniFile.ReadString("系统参数", "保存文件夹天数", "不清理");
            Globals.SoftwareVersion = Globals.IniFile.ReadString("软件参数", "软件版本", "V2.0 (.NET 10)");
            Globals.SYTime = Globals.IniFile.ReadString("软件参数", "试用时间", "100");
            _logger.LogInformation("读取配置成功，协议: {Protocol}, 地址: {Host}:{Port}", Globals.Protocol, Globals.IpAddress, Globals.Port);
        }

        private void IniPlcClient()
        {
            var variables = MiniExcel.Query<PLCVarConfigModel>(Globals.PlcVarConfigPath).ToList();
            if (variables.Count == 0)
                throw new InvalidOperationException("PLC变量表为空。");
            Globals.ReadDic.Clear();
            Globals.WriteDic.Clear();
            Globals.DataDic.Clear();
            Globals.SaveList.Clear();
            AlarmList.Clear();
            Globals.HasPlcData = false;
            foreach (var variable in variables)
            {
                var type = Enum.Parse<DataTypeEnum>(variable.变量类型, true);
                Globals.ReadDic.Add(variable.PLC地址, type);
                Globals.WriteDic.Add(variable.名称, variable.PLC地址);
                Globals.DataDic[variable.名称] = 0;
                if (string.Equals(variable.是否保存, "true", StringComparison.OrdinalIgnoreCase) || variable.是否保存 == "1")
                    Globals.SaveList.Add(variable.名称);
                if (variable.名称.EndsWith("报警") && !variable.名称.Contains("累计"))
                    AlarmList.Add(variable.名称);
            }
            Globals.PlcClient = Globals.Protocol == "ModbusTcp"
                ? new ModbusTcpPlcClient(Globals.IpAddress, Globals.Port, Globals.StationId, Globals.ConnectTimeOut, Globals.ModbusWordOrder)
                : new SiemensPlcClient(Globals.CpuType, Globals.IpAddress, Globals.Port, Globals.Slot, Globals.Rack, Globals.ConnectTimeOut);
            _ = Task.Run(() => RunPlcLoopAsync(variables, cts.Token));
        }

        private async Task RunPlcLoopAsync(List<PLCVarConfigModel> variables, CancellationToken token)
        {
            bool firstAttempt = true;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = Globals.PlcClient;
                    if (!client.Connected)
                    {
                        var connection = client.Open();
                        token.ThrowIfCancellationRequested();
                        if (!connection.IsSucceed)
                            _logger.LogDebug("PLC连接失败: {Message}", connection.ErrorMessage);
                    }
                    if (client.Connected)
                    {
                        var result = client.BatchRead(Globals.ReadDic);
                        token.ThrowIfCancellationRequested();
                        if (result.IsSucceed)
                        {
                            foreach (var variable in variables)
                                Globals.DataDic[variable.名称] = result.Value[variable.PLC地址];
                            Globals.HasPlcData = true;
                        }
                        else
                        {
                            Globals.HasPlcData = false;
                            client.Close();
                            _logger.LogWarning("PLC读取失败: {Message}", result.ErrorMessage);
                        }
                    }
                    else
                    {
                        Globals.HasPlcData = false;
                    }
                    if (!token.IsCancellationRequested && IsHandleCreated && !IsDisposed)
                    {
                        bool closeLoading = firstAttempt;
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            this.led_PlcState.On = Globals.HasPlcData && client.Connected;
                            if (!Globals.HasPlcData)
                            {
                                this.led_ProducteState.On = false;
                                this.led_ProducteState.Blink = false;
                                this.st_AlarmInfo.Text = client.Connected ? "PLC已连接，等待有效数据" : "PLC未连接，正在重试";
                                this.st_AlarmInfo.ForeColor = Color.OrangeRed;
                            }
                            if (closeLoading)
                                Globals.ServiceProvider.GetRequiredService<FromStartLoad>().CloseFormThread();
                        }));
                    }
                    firstAttempt = false;
                    await Task.Delay(Globals.HasPlcData ? Globals.ReadTimeInterval : Globals.ReConnectTimeInterval, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Globals.HasPlcData = false;
                _logger.LogError(ex, "PLC采集任务异常退出");
                if (!token.IsCancellationRequested && IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        this.led_PlcState.On = false;
                        this.st_AlarmInfo.Text = "PLC采集失败，请检查日志和配置";
                        Globals.ServiceProvider.GetRequiredService<FromStartLoad>().CloseFormThread();
                    }));
                }
            }
            finally
            {
                Globals.HasPlcData = false;
                Globals.PlcClient.Close();
            }
        }

        private void InitAsideUI()
        {

            //设置关联
            Aside.TabControl = MainTabControl;
            int pageIndex = 1000;
            TreeNode parent0 = Aside.CreateNode("控制模块", 361461, 34, pageIndex);
            Aside.CreateChildNode(parent0, AddPage(Globals.ServiceProvider.GetRequiredService<PageTotalEquipmentControl>(), ++pageIndex));

            TreeNode parent1 = Aside.CreateNode("用户模块", 61447, 34, pageIndex);
            Aside.CreateChildNode(parent1, AddPage(Globals.ServiceProvider.GetRequiredService<PageUserManage>(), ++pageIndex));
            Aside.CreateChildNode(parent1, AddPage(Globals.ServiceProvider.GetRequiredService<PageAuthManage>(), ++pageIndex));

            TreeNode parent2 = Aside.CreateNode("监控模块", 560066, 34, pageIndex);
            Aside.CreateChildNode(parent2, AddPage(Globals.ServiceProvider.GetRequiredService<PageEquipmentMonitor>(), ++pageIndex));
            Aside.CreateChildNode(parent2, AddPage(Globals.ServiceProvider.GetRequiredService<PageEquipmentMonitor1>(), ++pageIndex));
            Aside.CreateChildNode(parent2, AddPage(Globals.ServiceProvider.GetRequiredService<PageEquipmentMonitor2>(), ++pageIndex));

            TreeNode parent3 = Aside.CreateNode("配方模块", 162677, 34, pageIndex);
            Aside.CreateChildNode(parent3, AddPage(Globals.ServiceProvider.GetRequiredService<PageRecipeManage>(), ++pageIndex));

            TreeNode parent4 = Aside.CreateNode("日志模块", 57557, 34, pageIndex);
            Aside.CreateChildNode(parent4, AddPage(Globals.ServiceProvider.GetRequiredService<PageLogManage>(), ++pageIndex));

            TreeNode parent5 = Aside.CreateNode("报表模块", 57586, 34, pageIndex);
            Aside.CreateChildNode(parent5, AddPage(Globals.ServiceProvider.GetRequiredService<PageReportManage>(), ++pageIndex));

            TreeNode parent6 = Aside.CreateNode("图表模块", 61950, 34, pageIndex);
            Aside.CreateChildNode(parent6, AddPage(Globals.ServiceProvider.GetRequiredService<PageChartManage>(), ++pageIndex));

            TreeNode parent7 = Aside.CreateNode("参数模块", 559576, 34, pageIndex);
            Aside.CreateChildNode(parent7, AddPage(Globals.ServiceProvider.GetRequiredService<PageSystemParameterSet>(), ++pageIndex));

            _logger.LogInformation("初始化侧边栏菜单成功");

        }

        private void InitHeaderUI()
        {
            //设置关联
            Header.TabControl = MainTabControl;

            Header.Nodes.Add("");
            Header.Nodes.Add("");
            Header.Nodes.Add("");
            Header.SetNodeSymbol(Header.Nodes[0], 558295, 34);
            Header.SetNodeSymbol(Header.Nodes[1], 61489, 34);
            Header.SetNodeSymbol(Header.Nodes[2], 557925, 34);
            var styles = UIStyles.PopularStyles();
            foreach (UIStyle style in styles)
            {
                Header.CreateChildNode(Header.Nodes[0], style.DisplayText(), style.Value());
            }

            //获取枚举FontsType的所有字体名称
            for (int i = 0; i < Enum.GetValues(typeof(SystemEnums.FontsType)).Length; i++)
            {
                Header.CreateChildNode(Header.Nodes[1], Enum.GetName(typeof(SystemEnums.FontsType), i), i + 1);
            }

            //获取枚举FontSize的所有字体大小  75-125的范围 75 80 85 90 95 100 105 110 115 120 125
            for (int i = 75; i <= 125; i += 5)
            {
                Header.CreateChildNode(Header.Nodes[2], i.ToString(), i);
            }

            _logger.LogInformation("初始化头部菜单成功");
        }
        #endregion

        #region 主题管理
        private void Header_MenuItemClick(string itemText, int menuIndex, int pageIndex)
        {
            switch (menuIndex)
            {
                case 0:
                    UIStyle style = (UIStyle)pageIndex;
                    if (pageIndex < UIStyle.Colorful.Value())
                    {
                        StyleManager.Style = style;
                        if (UIExtension.SetStyleManager != null)
                        {
                            UIExtension.SetStyleManager(StyleManager);
                        }
                    }
                    break;

                case 1:
                    UIStyles.DPIScale = true;
                    UIStyles.GlobalFont = true;
                    UIStyles.GlobalFontName = itemText;

                    UIStyles.GlobalFontScale = SystemConsts.DefaultFontScale;
                    UIStyles.SetDPIScale();
                    break;

                case 2:
                    UIStyles.GlobalFontScale = int.Parse(itemText);
                    UIStyles.SetDPIScale();
                    break;

                default:

                    break;
            }
        }

        #endregion

        #region 关闭和最小化

        private void lbl_Min_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }

        private void lbl_Exit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        #endregion

        #region 无边框拖动

        private Point mPoint;

        private void Panel_MouseDown(object sender, MouseEventArgs e)
        {
            mPoint = new Point(e.X, e.Y);
        }

        private void Panel_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                this.Location = new Point(this.Location.X + e.X - mPoint.X, this.Location.Y + e.Y - mPoint.Y);
            }
        }
        #endregion

        private void pictureBox2_Click(object sender, EventArgs e)
        {
            //把Aside折叠起来
            Aside.CollapseAll();
            Aside.SelectFirst();

            var frmLogin = Globals.ServiceProvider.GetRequiredService<FrmLogin>();
            frmLogin.ShowDialog();
            if (frmLogin.IsLogin)
            {
                //更新登录用户
                this.lbl_User.Text = frmLogin.UserName;
                foreach (var control in pageControls.Values)
                {
                    control.Enabled = true;
                }
            }


        }

        private async void Aside_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            string moduleName = e.Node.Text;

            string user = this.lbl_User.Text;
            var roleRes = await _userManager.GetUserAuthAsync(new QueryUserAuthDto { UserName = user });
            if (roleRes.Status == SystemEnums.Result.Success)
            {
                if (roleRes.Data[0].Role != "管理员")
                {
                    var authRes = await _authManager.GetAuthAsync(new QueryAuthDto() { Role = roleRes.Data[0].Role });
                    if (authRes.Status == SystemEnums.Result.Success)
                    {
                        UpdateControlAccess(moduleName, authRes.Data[0], pageControls);
                    }
                }
            }

        }

        private void UpdateControlAccess(string moduleName, QueryAuthResultDto authDto, Dictionary<string, Control> pageControls)
        {
            switch (moduleName)
            {
                case "控制模块":
                    pageControls["控制模块"].Enabled = authDto.ControlModule;
                    break;

                case "用户模块":
                    pageControls["用户模块"].Enabled = false;
                    pageControls["权限模块"].Enabled = false;
                    break;

                case "监控模块":
                    pageControls["监控模块"].Enabled = authDto.MonitorModule;
                    pageControls["监控模块1"].Enabled = authDto.MonitorModule;
                    pageControls["监控模块2"].Enabled = authDto.MonitorModule;
                    break;

                case "配方模块":
                    pageControls["配方模块"].Enabled = authDto.RecipeModule;
                    break;

                case "日志模块":
                    pageControls["日志模块"].Enabled = authDto.LogModule;
                    break;

                case "报表模块":
                    pageControls["报表模块"].Enabled = authDto.ReportModule;
                    break;

                case "图表模块":
                    pageControls["图表模块"].Enabled = authDto.ChartModule;
                    break;

                case "参数模块":
                    pageControls["参数模块"].Enabled = authDto.ParamModule;
                    break;

                default:
                    break;
            }
        }

        private void FrmMain_Shown(object sender, EventArgs e)
        {
            Globals.ServiceProvider.GetRequiredService<FromStartLoad>().OpenFormThread();
            try
            {
                Init();
            }
            catch (Exception ex)
            {
                Globals.ServiceProvider.GetRequiredService<FromStartLoad>().CloseFormThread();
                _logger.LogError(ex, "初始化失败");
                UIMessageBox.ShowError("初始化失败：" + ex.Message);
            }
        }
        #region 窗体切换卡顿处理
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams paras = base.CreateParams;
                paras.ExStyle |= 0x02000000;
                return paras;
            }
        }
        #endregion

    }
}
