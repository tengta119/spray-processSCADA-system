using IoTClient.Clients.PLC;
using IoTClient.Common.Enums;
using IoTClient.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sunny.UI;
using System.Collections.Concurrent;
using Yale.SprayProcessSCADASystem.Communication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yale.SprayProcessSCADASystem
{
    public static class Globals
    {
        public static ServiceProvider ServiceProvider = null!;
        public static IniFile IniFile = new IniFile(Application.StartupPath + "\\config.ini");

        public static string PlcVarConfigPath = string.Empty;

        public static string DelFilePath = string.Empty;

        public static string SaveDay = string.Empty;
        public static string SoftwareVersion = string.Empty;
        public static string SYTime = string.Empty;

        public static bool IsAuthXk;



        public static IPlcClient PlcClient = null!;
        public static string Protocol = "ModbusTcp";
        public static byte StationId = 1;
        public static string ModbusWordOrder = "ABCD";
        public static volatile bool HasPlcData;
        //CPU类型
        public static SiemensVersion CpuType;
        //IP地址
        public static string IpAddress = string.Empty;
        //端口号
        public static int Port;
        //插槽号
        public static byte Slot;
        //机架号
        public static byte Rack;
        //PLC连接超时时间
        public static int ConnectTimeOut;
        //PLC读取间隔时间
        public static int ReadTimeInterval;
        //PLC重连时间间隔
        public static int ReConnectTimeInterval;
        //PLC变量字典
        public static Dictionary<string, DataTypeEnum> ReadDic = new Dictionary<string, DataTypeEnum>();
        //PLC变量值 读取的 字典
        public static ConcurrentDictionary<string, object> DataDic = new ConcurrentDictionary<string, object>();
        //PLC变量 写入字典 
        public static Dictionary<string, string> WriteDic = new Dictionary<string, string>();
        //PLC 需要保存的变量集合
        public static List<string> SaveList = new List<string>();

        public static bool PlcWrite(string varName, object value, DataTypeEnum dataType= DataTypeEnum.Bool)
        {
            if (PlcClient != null && PlcClient.Connected && WriteDic.TryGetValue(varName, out var address))
            {
                // The variable table is authoritative, including Int32 recipe values.
                var configuredType = ReadDic.TryGetValue(address, out var type) ? type : dataType;
                var res = PlcClient.Write(address, value, configuredType);
                if (res.IsSucceed)
                {
                    return true;
                }

                return false;
            }
            return false;

        }

        #region 获取所有控件
        // 获取指定页面的所有控件
        public static List<Control> GetAllControls(UIPage page)
        {
            List<Control> allControls = new List<Control>();
            CollectControls(page.Controls, allControls);
            return allControls;
        }

        // 递归方法，用于收集控件
        private static void CollectControls(Control.ControlCollection controls, List<Control> allControls)
        {
            foreach (Control control in controls)
            {
                allControls.Add(control); // 将当前控件添加到列表中
                                          // 如果当前控件有子控件，则递归调用此方法
                if (control.HasChildren)
                {
                    CollectControls(control.Controls, allControls);
                }
            }
        }
        #endregion


    }
}
