# 喷涂工艺 SCADA 系统

Windows 桌面上位机，监控脱脂、粗洗、陶化、精洗、水分炉、冷却室、固化炉和输送机。支持设备控制、配方下发、历史报表、趋势曲线、日志与用户权限。当前项目使用 .NET 10，可通过 Modbus TCP 连接同一台电脑上的 Modbus Slave，也保留西门子 S7 通信。

## 本机启动

本机已经安装 .NET 10 SDK。在项目根目录打开 PowerShell，执行：

```powershell
.\start.ps1
```

如果 PowerShell 的脚本执行策略阻止运行，可仅对本次启动执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\start.ps1
```

启动脚本会还原依赖并编译，随后运行 `Yale.SprayProcessSCADASystem/bin/Debug/net10.0-windows/` 中的程序。首次编译需要访问 NuGet。也可以在 Visual Studio 中打开 `Yale.SprayProcessSCADASystem.sln`，将同名 WinForms 项目设为启动项目后调试。

新版本在自己的运行目录使用 SQLite 数据库，不自动导入旧 `net6.0-windows7.0` 目录的数据。首次启动自动建表并初始化工程师、操作员和访客的权限，没有预设管理员账号或密码。先以访客进入“用户模块 → 用户管理”，填写自定账号、密码和确认密码，角色选择“管理员”，点击“添加用户”，再点击顶部用户头像登录。

## 用 Modbus Slave 模拟设备

按照 [Modbus Slave 官方手册](https://www.modbustools.com/mbslave-user-manual.html)设置：按 `F3`，选择 **Modbus TCP/IP**，监听地址 `127.0.0.1`，端口 `502`。不要选择 RTU Over TCP/IP。

按 `Ctrl+N` 新建两个数据窗口，分别按 `F8` 设置以下参数，两窗均使用 **Slave ID = 1**：

| 窗口 | Function | Address | Quantity | 用途 |
| --- | --- | ---: | ---: | --- |
| 线圈 | 01 Read Coils (0x) | 0 | 50 | 开关、运行状态、报警 |
| 保持寄存器 | 03 Read Holding Registers (4x) | 0 | 56 | 压力、pH、温湿度、计数、配方 |

两个窗口需要同时保持打开，再启动 SCADA。默认配置使用 Modbus TCP、站号 1 和独立的 `PLC_Modbus_Config.xlsx` 变量表。未启动 Modbus Slave 时，SCADA 可以打开界面并自动重连。

地址使用 **从 0 开始的协议地址**：变量表的 `C:0` 表示第一个线圈，`HR:0` 表示第一个保持寄存器。关闭 Modbus Slave 的 **PLC Addresses (Base 1)** 可直接按本文地址找数据；若启用，`HR:0` 会显示为 `40001`。

## 验证读写

双击 Modbus Slave 的值可修改模拟数据。下面的例子都采用 **ABCD / Big-endian** 字节顺序。`Float` 和 `Int32` 各占两个 16 位寄存器，寄存器内高字节在前、高位寄存器在前。

| 想验证的功能 | 操作 | 预期结果 |
| --- | --- | --- |
| 读取温度 | 03 窗口将地址 16、17 的 Hex 值设为 `41C8`、`0000` | SCADA 厂内温度显示 25 ℃ |
| 读取生产计数 | 地址 20、21 的整数值设为 0、100 | 生产计数显示 100 |
| 设备运行状态 | 01 窗口将地址 21 设为 1 | 脱脂喷淋泵显示运行 |
| 报警 | 将线圈 23 设为 1，随后改回 0 | 脱脂槽液位报警出现，随后消失 |
| 写入启动命令 | 点击 SCADA 的“总启动” | Modbus Slave 线圈 0 变为 1 |
| 配方下发 | 在 SCADA 编辑配方并下发 | 03 窗口地址 28～55 对应参数改变 |

Float 数据可选择 Modbus Slave 的 **Float Big-endian** 显示格式，直接输入 `25`。Int32 计数应使用 **Signed integer Big-endian** 格式。不要将整张寄存器窗口统一显示成 Float，否则 Int32 计数和配方整数会显示错误。

完整的变量、地址、类型和占用范围见 [Modbus 地址映射](docs/Modbus地址映射.md)。原 `PLC_Var_Config.xlsx` 保留 S7 地址，不能直接用于 Modbus。

Modbus Slave 提供可读写的数据区，不运行原生产线的 PLC 逻辑。点击“总启动”只写入命令线圈，不会自动改变泵运行状态、累计产量或执行温控。演示这些反馈时，可手工修改相应状态和数值，或另外编写仿真逻辑。命令线圈也不会自行复位，需要在模拟器中手动清零。

## 配置与排查

默认运行配置的 PLC 部分如下，`ABCD` 同时用于 Float 和 Int32：

```ini
[PLC参数]
通信协议=ModbusTcp
PLC地址=127.0.0.1
PLC端口=502
变量表地址=PLC_Modbus_Config.xlsx
站号=1
浮点字序=ABCD
超时时间=3000
循环读取时间=500
重连时间=3000
```

切换回西门子 PLC 时，将通信协议设为 `SiemensS7`，选用原 `PLC_Var_Config.xlsx`，并设置实际 PLC 的地址、端口、CPU 类型、机架号与插槽号。

- 程序运行目录的 `config.ini` 保存通信参数，界面“系统参数”中修改后重启生效。若端口 502 被占用，可让双方使用另一个相同端口，例如 1502。
- 报“连接失败”时，确认 Modbus Slave 已连接、TCP 监听端口一致、Slave ID 为 1，并同时打开上述两个数据区。
- TCP 已连接但读数失败时，确认两个数据区的 Address 和 Quantity；未定义所需数据区会导致 Modbus 异常响应。
- 数值异常时，先核对变量类型、两寄存器的起始地址，以及双方字节顺序。
- 编译提示旧电脑的 NuGet 回退目录不存在时，重新执行 `dotnet restore .\Yale.SprayProcessSCADASystem.sln --force`，再启动。
- 日志位于程序运行目录的 `Logs` 文件夹。不要只复制 EXE，配置、变量表和依赖 DLL 需要保留在同一运行目录。

## 开发验证

先编译主项目，再执行通信测试：

```powershell
dotnet build .\Yale.SprayProcessSCADASystem.sln
dotnet run --project .\tests\PlcTransportChecks\PlcTransportChecks.csproj
python .\tests\verify_app.py
```

通信测试使用回环服务器验证读写、四种字节序、异常响应、超时重连和并发。Python 检查需要 Windows 和 Python 3，仅使用标准库；它在临时副本中验证完整程序采集、数据库写入、断线重连和离线启动，保留临时数据库和日志供复核，不连接真实 PLC。
