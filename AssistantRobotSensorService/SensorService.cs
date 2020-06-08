using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

using LogPrinter;

namespace AssistantRobotSensorService
{
    public partial class SensorService : ServiceBase
    {
        protected ServiceFunction sf;
        protected bool ifLoadedSuccess = true;
        protected bool ifCloseFromServiceFunction = false;

        public SensorService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            sf = new ServiceFunction(out ifLoadedSuccess);
            if (!ifLoadedSuccess)
            {
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service close at initial pos.");
                Stop();
                return;
            }

            sf.OnSendCloseService += sf_OnSendCloseService;
            sf.StartListenLoop();
        }

        protected void sf_OnSendCloseService()
        {
            ifCloseFromServiceFunction = true;
            Stop();
        }

        protected override void OnStop()
        {
            if (ifLoadedSuccess && !ifCloseFromServiceFunction)
            {
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service close from outer side.");

                sf.StopListenLoop().Wait();
                Logger.HistoryPrinting(Logger.Level.INFO, MethodBase.GetCurrentMethod().DeclaringType.FullName, "Sensor service ready to closed.");
            }
        }
    }
}
