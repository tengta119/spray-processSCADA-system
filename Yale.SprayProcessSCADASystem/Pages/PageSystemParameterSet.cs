using YR.FrameWork.DependencyInjection;
using IoTClient.Common.Enums;
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

namespace Yale.SprayProcessSCADASystem
{
    public partial class PageSystemParameterSet : UIPage, ISingletonSelfDependency
    {
        private bool initializingParameters;
        private string previousProtocol = "ModbusTcp";
        private static readonly string[] ModbusWordOrders = { "ABCD", "CDAB", "BADC", "DCBA" };

        public PageSystemParameterSet()
        {
            InitializeComponent();
            this.Load += PageSystemParameterSet_Load;
        }

        private void PageSystemParameterSet_Load(object? sender, EventArgs e)
        {
            InitSystemParameter();
        }

        private void InitSystemParameter()
        {
            initializingParameters = true;
            this.cb_Protocol.Items.Clear();
            this.cb_Protocol.Items.AddRange(new object[] { "Modbus TCP", "西门子 S7" });
            this.cb_Protocol.SelectedIndex = Globals.Protocol == "SiemensS7" ? 1 : 0;
            previousProtocol = SelectedProtocol;

            this.txt_StationId.Text = Globals.StationId.ToString();
            this.cb_ModbusWordOrder.Items.Clear();
            this.cb_ModbusWordOrder.Items.AddRange(ModbusWordOrders);
            this.cb_ModbusWordOrder.SelectedItem = Globals.ModbusWordOrder;

            this.txt_PLCVarAddressPath.Text = Globals.PlcVarConfigPath;

            this.txt_IPAddress.Text = Globals.IpAddress;
            this.txt_Port.Text = Globals.Port.ToString();
            this.cb_CPUType.Items.Clear();
            this.cb_CPUType.Items.AddRange(Enum.GetNames(typeof(SiemensVersion)));
            this.cb_CPUType.Text = Globals.CpuType.ToString();

            this.txt_Rack.Text = Globals.Rack.ToString();
            this.txt_Slot.Text = Globals.Slot.ToString();

            this.txt_ConnectTimeOut.Text = Globals.ConnectTimeOut.ToString();
            this.txt_ReadTimeInterval.Text=Globals.ReadTimeInterval.ToString();
            this.txt_ReConnectTimeInterval.Text=Globals.ReConnectTimeInterval.ToString();

            this.txt_DirePath.Text = Globals.DelFilePath;

            switch(Globals.SaveDay)
            {
                case "不清理":
                    this.rbg_Save.SelectedIndex = 0;
                    break;
                case "3天":
                    this.rbg_Save.SelectedIndex = 1;
                    break;
                case "7天":
                    this.rbg_Save.SelectedIndex = 2;
                    break;
                case "15天":
                    this.rbg_Save.SelectedIndex = 3;
                    break;
                case "30天":
                    this.rbg_Save.SelectedIndex = 4;
                    break;
                case "60天":
                    this.rbg_Save.SelectedIndex = 5;
                    break;
            }

            this.txt_SoftwareVersion.Text = Globals.SoftwareVersion;
            this.txt_SoftTime.Text = Globals.SYTime;
            initializingParameters = false;
            UpdateProtocolControls();
        }

        private string SelectedProtocol => this.cb_Protocol.SelectedIndex == 1 ? "SiemensS7" : "ModbusTcp";

        private static string DefaultVariablePath(string protocol) => Path.Combine(Application.StartupPath,
            protocol == "ModbusTcp" ? "PLC_Modbus_Config.xlsx" : "PLC_Var_Config.xlsx");

        private void cb_Protocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (initializingParameters)
            {
                return;
            }

            string protocol = SelectedProtocol;
            string previousPort = previousProtocol == "ModbusTcp" ? "502" : "102";
            if (string.IsNullOrWhiteSpace(this.txt_Port.Text) || this.txt_Port.Text.Trim() == previousPort)
            {
                this.txt_Port.Text = protocol == "ModbusTcp" ? "502" : "102";
            }

            string currentPath = this.txt_PLCVarAddressPath.Text.Trim();
            bool isDefaultPath = string.IsNullOrEmpty(currentPath);
            if (!isDefaultPath)
            {
                try
                {
                    isDefaultPath = string.Equals(Path.GetFullPath(currentPath, Application.StartupPath),
                        DefaultVariablePath(previousProtocol), StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            if (isDefaultPath)
            {
                this.txt_PLCVarAddressPath.Text = DefaultVariablePath(protocol);
            }

            previousProtocol = protocol;
            UpdateProtocolControls();
        }

        private void UpdateProtocolControls()
        {
            bool isModbus = SelectedProtocol == "ModbusTcp";
            this.cb_CPUType.Enabled = !isModbus;
            this.txt_Rack.Enabled = !isModbus;
            this.txt_Slot.Enabled = !isModbus;
            this.txt_StationId.Enabled = isModbus;
            this.cb_ModbusWordOrder.Enabled = isModbus;
        }

        private bool ValidateParameters()
        {
            if (this.cb_Protocol.SelectedIndex < 0)
            {
                UIMessageTip.ShowWarning("请选择通信协议");
                return false;
            }
            if (!System.Net.IPAddress.TryParse(this.txt_IPAddress.Text.Trim(), out _))
            {
                UIMessageTip.ShowWarning("请输入有效的PLC IP地址");
                return false;
            }
            if (!int.TryParse(this.txt_Port.Text, out int port) || port < 1 || port > 65535)
            {
                UIMessageTip.ShowWarning("端口号必须为1至65535的整数");
                return false;
            }
            if (!byte.TryParse(this.txt_StationId.Text, out byte stationId) || stationId < 1 || stationId > 247)
            {
                UIMessageTip.ShowWarning("Modbus站号必须为1至247的整数");
                return false;
            }
            if (!ModbusWordOrders.Contains(this.cb_ModbusWordOrder.Text))
            {
                UIMessageTip.ShowWarning("请选择有效的浮点字序");
                return false;
            }
            if (SelectedProtocol == "SiemensS7" &&
                (!Enum.TryParse<SiemensVersion>(this.cb_CPUType.Text, out _) ||
                 !byte.TryParse(this.txt_Rack.Text, out _) || !byte.TryParse(this.txt_Slot.Text, out _)))
            {
                UIMessageTip.ShowWarning("请选择CPU类型，机架号和插槽号必须为0至255的整数");
                return false;
            }
            if (!int.TryParse(this.txt_ConnectTimeOut.Text, out int timeout) || timeout <= 0 ||
                !int.TryParse(this.txt_ReadTimeInterval.Text, out int readInterval) || readInterval <= 0 ||
                !int.TryParse(this.txt_ReConnectTimeInterval.Text, out int reconnectInterval) || reconnectInterval <= 0)
            {
                UIMessageTip.ShowWarning("连接超时、读取间隔和重连间隔必须为正整数，单位为毫秒");
                return false;
            }
            if (!File.Exists(this.txt_PLCVarAddressPath.Text.Trim()))
            {
                UIMessageTip.ShowWarning("PLC变量表文件不存在，请填写有效路径");
                return false;
            }
            if (!Directory.Exists(this.txt_DirePath.Text.Trim()))
            {
                UIMessageTip.ShowWarning("删除文件夹不存在");
                return false;
            }
            if (this.rbg_Save.SelectedIndex < 0)
            {
                UIMessageTip.ShowWarning("请选择日志保存天数");
                return false;
            }
            return true;
        }

        private void btn_Save_Click(object sender, EventArgs e)
        {
            if (!ValidateParameters())
            {
                return;
            }

            var parameters = new Dictionary<string, string>
            {
                {"通信协议", SelectedProtocol },
                {"站号", this.txt_StationId.Text.Trim() },
                {"浮点字序", this.cb_ModbusWordOrder.Text },
                {"PLC地址",this.txt_IPAddress.Text.Trim() },
                {"变量表地址",this.txt_PLCVarAddressPath.Text.Trim() },
                {"PLC端口" ,this.txt_Port.Text.Trim()},
                 { "CPU类型", this.cb_CPUType.Text },
                { "机架号", this.txt_Rack.Text },
                { "插槽号", this.txt_Slot.Text },
                { "超时时间",this.txt_ConnectTimeOut.Text },
                { "循环读取时间",this.txt_ReadTimeInterval.Text},
                { "重连时间",this.txt_ReConnectTimeInterval.Text }
        };

            foreach(var parameter in parameters)
            {
                if(!Globals.IniFile.Write("PLC参数",parameter.Key, parameter.Value))
                {
                    UIMessageTip.ShowWarning($"保存{parameter.Key}失败！");
                    return;
                }
            }

            string delDirePath = this.txt_DirePath.Text.Trim();
            string saveDay = this.rbg_Save.Items[this.rbg_Save.SelectedIndex].ToString();

            if(Directory.Exists(delDirePath))
            {
                if(!Globals.IniFile.Write("系统参数","删除文件夹路径",delDirePath))
                {
                    UIMessageTip.ShowWarning("删除文件夹路径失败");
                    return;
                }
            }
            else
            {
                UIMessageTip.ShowWarning("删除文件夹不存在");
                return;
            }

            if (!Globals.IniFile.Write("系统参数", "保存文件夹天数", saveDay))
            {
                UIMessageTip.ShowWarning("保存天数失败");
                return;
            }

            if (!Globals.IniFile.Write("软件参数", "软件版本", this.txt_SoftwareVersion.Text))
            {
                UIMessageTip.ShowWarning("保存软件版本信息失败！");
                return;
            }

            if (!Globals.IniFile.Write("软件参数", "试用时间", this.txt_SoftTime.Text))
            {
                UIMessageTip.ShowWarning("保存软件试用时间失败！");
                return;
            }

            UIMessageTip.ShowOk("保存成功，通信参数将在重启软件后生效！");


        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams paras = base.CreateParams;
                paras.ExStyle |= 0x02000000;
                return paras;
            }
        }
    }
}
