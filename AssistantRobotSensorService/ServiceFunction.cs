using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Drawing;
using System.Configuration;

using LogPrinter;
using SensorAgent;

namespace AssistantRobotSensorService
{
    public class ServiceFunction
    {
        #region 字段
        private readonly bool ifAtSamePC = true;

        private const int clientPortTCP = 30400;
        private const string serverIP = "127.0.0.1";
        private const int serverPortTCP = 40400;

        private Socket tcpListenSocket;
        private CancellationTokenSource tcpListenCancel;
        private Task tcpListenTask;

        private Socket tcpTransferSocket;
        private bool tcpTransferSocketEstablished = false;
        private readonly int tcpTransferSocketSendTimeOut = 500;

        public delegate void SendCloseService();
        public event SendCloseService OnSendCloseService;
        private static readonly object closeSideLocker = new object();
        private bool lockCloseSide = false;
        private bool ifCloseFromInnerSide = true;

        private System.Timers.Timer checkTimer;
        private readonly int checkPeriod = 200;
        private bool isInTimer = false;

        private SensorBase sb;
        #endregion

        #region 方法
        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="ifSuccessConstructed">是否成功构造</param>
        public ServiceFunction(out bool ifSuccessConstructed)
        {
            // 检查环境
            if (!Functions.CheckEnvironment())
            {
                ifSuccessConstructed = false;
                return;
            }
            Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service starts with successful checked.");

            // 加载程序配置
            bool parseResult = true;

            int checkPeriodTemp;
            parseResult = int.TryParse(ConfigurationManager.AppSettings["checkPeriod"], out checkPeriodTemp);
            if (parseResult) checkPeriod = checkPeriodTemp;
            else
            {
                ifSuccessConstructed = false;
                Logger.HistoryPrinting(Logger.Level.WARN, MethodBase.GetCurrentMethod().DeclaringType.FullName, "App configuration parameter(" + "checkPeriod" + ") is wrong.");
                return;
            }

            int tcpTransferSocketSendTimeOutTemp;
            parseResult = int.TryParse(ConfigurationManager.AppSettings["tcpTransferSocketSendTimeOut"], out tcpTransferSocketSendTimeOutTemp);
            if (parseResult) tcpTransferSocketSendTimeOut = tcpTransferSocketSendTimeOutTemp;
            else
            {
                ifSuccessConstructed = false;
                Logger.HistoryPrinting(Logger.Level.WARN, MethodBase.GetCurrentMethod().DeclaringType.FullName, "App configuration parameter(" + "tcpTransferSocketSendTimeOut" + ") is wrong.");
                return;
            }

            // 初始化Sensor
            sb = new SensorBase(true, true, true);
            sb.OnSendSenorDatas += sb_OnSendSenorDatas;
            sb.OnSendSenorDataWrong += sb_OnSendSenorDataWrong;

            // 初始化定时器
            checkTimer = new System.Timers.Timer(checkPeriod);
            checkTimer.AutoReset = true;
            checkTimer.Elapsed += checkTimer_Elapsed;
            checkTimer.Start();

            ifSuccessConstructed = true;
        }

        /// <summary>
        /// 检查设备是否存在
        /// </summary>
        void checkTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (isInTimer) return;
            isInTimer = true;

            if (sb.CheckIfDevicesAllExist())
            { // 存在则连接
                if (sb.ConnectDevices())
                    checkTimer.Stop();
            }

            isInTimer = false;
        }

        /// <summary>
        /// 开始监听循环
        /// </summary>
        public void StartListenLoop()
        {
            // TCP侦听循环开始
            tcpListenCancel = new CancellationTokenSource();
            tcpListenTask = new Task(() => TcpListenTaskWork(tcpListenCancel.Token));
            tcpListenTask.Start();
        }

        /// <summary>
        /// 关闭监听循环
        /// </summary>
        /// <returns>返回监听循环任务</returns>
        public Task StopListenLoop()
        {
            lock (closeSideLocker)
            {
                if (!lockCloseSide) ifCloseFromInnerSide = false;
                lockCloseSide = true;
            }

            EndAllLoop();
            return tcpListenTask;
        }

        /// <summary>
        /// TCP监听任务
        /// </summary>
        /// <param name="cancelFlag">停止标志</param>
        private void TcpListenTaskWork(CancellationToken cancelFlag)
        {
            Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service tcp listener begins.");

            while (true)
            {
                if (cancelFlag.IsCancellationRequested) break;

                // TCP侦听socket建立 开始侦听
                tcpListenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcpListenSocket.Bind(new IPEndPoint(IPAddress.Parse(serverIP), serverPortTCP));
                tcpListenSocket.Listen(1);
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service tcp listener begins to listen.");

                // TCP侦听socket等待连接建立
                IAsyncResult acceptResult = tcpListenSocket.BeginAccept(null, null);
                do
                {
                    if (cancelFlag.IsCancellationRequested) break;
                    acceptResult.AsyncWaitHandle.WaitOne(1000, true);  //等待1秒
                } while (!acceptResult.IsCompleted);
                if (cancelFlag.IsCancellationRequested) // 不再accept等待
                    break;
                tcpTransferSocket = tcpListenSocket.EndAccept(acceptResult);
                tcpTransferSocket.SendTimeout = tcpTransferSocketSendTimeOut;
                tcpTransferSocketEstablished = true;
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service tcp transfer connection is established.");

                // TCP连接建立之后关闭侦听socket
                tcpListenSocket.Close();
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service tcp listener is closed.");

                // 等待直到TCP连接断开
                while (tcpTransferSocketEstablished) Thread.Sleep(1000);

                // 准备再次进行监听
                Thread.Sleep(1000);
            }

            Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service tcp listener stops.");

            // 关闭与传感器的连接，不计断开结果
            sb.DisconnectDevices();

            if (ifCloseFromInnerSide)
            {
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service close from inner side.");
                OnSendCloseService();
            }
        }

        /// <summary>
        /// 接收Sensor数据并转发
        /// </summary>
        /// <param name="array">接收的Sensor数据</param>
        void sb_OnSendSenorDatas(double[] array)
        {
            if (!tcpTransferSocketEstablished) return;

            try
            {
                DealWithTcpTransferSendDatas(array);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.ConnectionAborted || ex.SocketErrorCode == SocketError.TimedOut)
                {
                    FinishAllConnection();
                    Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service tcp transfer sends datas failed.");
                }
                else
                {
                    lock (closeSideLocker)
                    {
                        if (!lockCloseSide) ifCloseFromInnerSide = true;
                        lockCloseSide = true;
                    }

                    EndAllLoop();
                    Logger.HistoryPrinting(Logger.Level.WARN, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Not deal exception.", ex);
                }
            }
        }

        /// <summary>
        /// 处理TCP发送的数据
        /// </summary>
        /// <param name="datas">所发数据</param>
        private void DealWithTcpTransferSendDatas(double[] datas)
        {
            List<byte> sendBuffer = new List<byte>(80);

            foreach (double element in datas)
            {
                sendBuffer.AddRange(
                    BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(
                    BitConverter.DoubleToInt64Bits(element))));
            }

            tcpTransferSocket.Send(sendBuffer.ToArray());
        }

        /// <summary>
        /// 传感器数据出错
        /// </summary>
        void sb_OnSendSenorDataWrong()
        {
            // 断开连接
            if (!sb.DisconnectDevices()) // 断开失败，准备退出服务
                EndAllLoop();
            else // 成功断开，继续检测传感器是否存在
                checkTimer.Start();
        }

        /// <summary>
        /// 结束所有循环等待
        /// </summary>
        private void EndAllLoop()
        {
            checkTimer.Stop(); // 停止检查设备
            while (isInTimer) Thread.Sleep(200);

            tcpListenCancel.Cancel(); // 不再监听
            FinishAllConnection(); // 断开TCP连接
        }

        /// <summary>
        /// 结束所有连接
        /// </summary>
        private void FinishAllConnection()
        {
            if (tcpTransferSocketEstablished)
            {
                tcpTransferSocket.Shutdown(SocketShutdown.Both);
                tcpTransferSocket.Close();
                tcpTransferSocketEstablished = false;
            }
        }
        #endregion
    }
}
