﻿using System;
using System.Diagnostics;

namespace xenwinsvc
{
    public class EventLogger
    {
        EventLog el;
        protected WmiSession wmisession;
        public EventLogger(WmiSession wmisession)
        {
            this.wmisession = wmisession;
            el = new EventLog();
            el.Source = "XenGuestAgent";
            if (!EventLog.SourceExists("XenGuestAgent"))
            {
                //Create New Log       
                EventLog.CreateEventSource("XenGuestAgent", "XenGuestAgentLog");
            }
        }

        public void addEvent(string message)
        {
            //Log Information  
            wmisession.Log(message);
            el.WriteEntry(message, EventLogEntryType.Information);
        }

        public void addException(string message)
        {
            //Log Exception  
            wmisession.Log(message);
            el.WriteEntry(message, EventLogEntryType.Error);
        }

        public void addWarning(string message)
        {
            //Log Warning
            wmisession.Log(message);
            el.WriteEntry(message, EventLogEntryType.Warning);
        }
    }
}
