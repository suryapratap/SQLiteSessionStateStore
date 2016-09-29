using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Littlefish.SQLiteSessionStateProvider
{
	internal static class ExceptionHelper
	{
		private static string _eventSource = "SQLiteSessionStateStore";
		private static string _eventLog = "Application";

		/// <summary>
		/// WriteToEventLog
		/// This is a helper function that writes exception detail to the
		/// event log. Exceptions are written to the event log as a security
		/// measure to ensure private database details are not returned to
		/// browser. If a method does not return a status or Boolean
		/// indicating the action succeeded or failed, the caller also
		/// throws a generic exception.
		/// </summary>
		internal static void WriteToEventLog(this Exception e, string action)
		{
			EventLog log = new EventLog();
			log.Source = _eventSource;
			log.Log = _eventLog;

			string message = "An exception occurred communicating with the data source.\n\n";
			message += "Action: " + action + "\n\n";
			message += "Exception: " + e.ToString();

			log.WriteEntry(message);
		}
	}
}