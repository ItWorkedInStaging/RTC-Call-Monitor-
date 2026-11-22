using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RtcCallMonitor.Configuration
{
    public class Application
    {
        public string[] LocalNetwork { get; set; }
        public string CallStartWebhook { get; set; }
        public string CallEndWebhook { get; set; }
        public int? CheckInterval { get; set; }
        public int? MinPacketRate { get; set; }
        // consecutive check intervals with call traffic before a call is reported as started (default 2)
        public int? CallStartIntervals { get; set; }
        // consecutive check intervals without call traffic before a call is reported as ended (default 3)
        public int? CallEndIntervals { get; set; }
    }
}
