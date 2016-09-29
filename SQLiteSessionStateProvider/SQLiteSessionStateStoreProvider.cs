using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Configuration.Provider;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Timers;
using System.Web;
using System.Web.Configuration;
using System.Web.SessionState;

namespace Littlefish.SQLiteSessionStateProvider
{
	public class SQLiteSessionStateStoreProvider : SessionStateStoreProviderBase
	{
		private SessionStateSection _config = null;
		private string _connectionString;
		private ConnectionStringSettings _connectionStringSettings;
		private const string _eventSource = "SQLiteSessionStateStore";
		private const string _eventLog = "Application";
		private const string _exceptionMessage = "An exception occurred. Please contact your administrator.";
		private Timer _cleanupTimer;

		/// <summary>
		/// If false, exceptions are thrown to the caller. If true,
		/// exceptions are written to the event log.
		/// </summary>
		public bool WriteExceptionsToEventLog { get; set; }

		/// <summary>
		/// The ApplicationName property is used to differentiate sessions
		/// in the data source by application.
		/// </summary>
		public string ApplicationName { get; private set; }

		/// <summary>
		/// Initialize values from web.config.
		/// </summary>
		public override void Initialize(string name, NameValueCollection config)
		{
			if (config == null)
				throw new ArgumentNullException("config");

			if (name == null || name.Length == 0)
				name = "SQLiteSessionStateStore";

			if (String.IsNullOrEmpty(config["description"]))
			{
				config.Remove("description");
				config.Add("description", "SQLite Session State Store provider");
			}

			// Initialize the abstract base class.
			base.Initialize(name, config);

			// Initialize the ApplicationName property.
			ApplicationName = System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath;

			// Get <sessionState> configuration element.
			Configuration cfg = WebConfigurationManager.OpenWebConfiguration(ApplicationName);
			_config = (SessionStateSection)cfg.GetSection("system.web/sessionState");

			// Initialize connection string.
			var databaseFile = config["databaseFile"];
			if (databaseFile == null || string.IsNullOrWhiteSpace(databaseFile))
				throw new ProviderException("Configuration 'databaseFile' must be specified for SqliteSessionStateStoreProvider.");

			//Try and map the database to the location on the server.
			//This will allow databse files to be specified as ~/Folder
			var currentContext = HttpContext.Current;
			if (currentContext != null && !Path.IsPathRooted(databaseFile))
			{
				databaseFile = Path.Combine(currentContext.Server.MapPath("/"), databaseFile);
			}

			_connectionString = "Data Source =" + databaseFile;

			if (config["connectionParameters"] != null)
			{
				_connectionString += ";" + config["connectionParameters"];
			}

			var schemagenerator = new SchemaGenerator(databaseFile);
			schemagenerator.Create();

			//Setup cleanup timer to remove old session data
			//The least possible timeout value is 1 minute
			_cleanupTimer = new Timer(60000);
			_cleanupTimer.Elapsed += (sender, e) => CleanUpExpiredData();

			// Initialize WriteExceptionsToEventLog
			var writeExceptionsToEventLog = config["writeExceptionsToEventLog"];
			if (writeExceptionsToEventLog != null && writeExceptionsToEventLog.Equals("true", StringComparison.OrdinalIgnoreCase))
				WriteExceptionsToEventLog = true;
		}

		// SessionStateStoreProviderBase members
		public override void Dispose() { }

		// SessionStateProviderBase.SetItemExpireCallback
		public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
		{
			return false;
		}

		// SessionStateProviderBase.SetAndReleaseItemExclusive
		public override void SetAndReleaseItemExclusive(HttpContext context, string id, SessionStateStoreData item, object lockId, bool newItem)
		{
			// Serialize the SessionStateItemCollection as a string.
			string sessItems = Serialize((SessionStateItemCollection)item.Items);

			IDbConnection conn = new SQLiteConnection(_connectionString);
			IDbCommand cmd;
			IDbCommand deleteCmd = null;

			if (newItem)
			{
				// SQLiteCommand to clear an existing expired session if it exists.
				deleteCmd = conn.CreateCommand();
				deleteCmd.CommandText = "DELETE FROM Sessions WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND Expires < @Expires";
				deleteCmd.AddParameter("@SessionId", id, 80);
				deleteCmd.AddParameter("@ApplicationName", ApplicationName, 255);
				deleteCmd.AddParameter("@Expires", DateTime.UtcNow);

				// SQLiteCommand to insert the new session item.
				cmd = conn.CreateCommand();
				cmd.CommandText = "INSERT INTO Sessions (SessionId, ApplicationName, Created, Expires, LockDate, LockId, Timeout, Locked, SessionItems, Flags) Values(@SessionId, @ApplicationName, @Created, @Expires, @LockDate, @LockId, @Timeout, @Locked, @SessionItems, @Flags)";
				cmd.AddParameter("@SessionId", id, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName, 255);
				cmd.AddParameter("@Created", DateTime.UtcNow);
				cmd.AddParameter("@Expires", DateTime.UtcNow.AddMinutes((Double)item.Timeout));
				cmd.AddParameter("@LockDate", DateTime.UtcNow);
				cmd.AddParameter("@LockId", 0);
				cmd.AddParameter("@Timeout", item.Timeout);
				cmd.AddParameter("@Locked", false);
				cmd.AddParameter("@SessionItems", sessItems, sessItems.Length);
				cmd.AddParameter("@Flags", 0);
			}
			else
			{
				// SQLiteCommand to update the existing session item.
				cmd = conn.CreateCommand();
				cmd.CommandText = "UPDATE Sessions SET Expires = @Expires, SessionItems = @SessionItems, Locked = @Locked WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND LockId = @LockId";
				cmd.AddParameter("@Expires", DateTime.UtcNow.AddMinutes((Double)item.Timeout));
				cmd.AddParameter("@SessionItems", sessItems, sessItems.Length);
				cmd.AddParameter("@Locked", false);
				cmd.AddParameter("@SessionId", id, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName);
				cmd.AddParameter("@LockId", (int)lockId);
			}

			try
			{
				conn.Open();

				if (deleteCmd != null)
					deleteCmd.ExecuteNonQuery();

				cmd.ExecuteNonQuery();
			}
			catch (SQLiteException e)
			{
				if (WriteExceptionsToEventLog)
				{
					e.WriteToEventLog("SetAndReleaseItemExclusive");
					throw new ProviderException(_exceptionMessage);
				}
				else
					throw e;
			}
			finally
			{
				conn.Close();
			}
		}

		/// <summary>
		/// SessionStateProviderBase.GetItem
		/// </summary>
		public override SessionStateStoreData GetItem(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actionFlags)
		{
			return GetSessionStoreItem(false, context, id, out locked, out lockAge, out lockId, out actionFlags);
		}

		/// <summary>
		/// SessionStateProviderBase.GetItemExclusive
		/// </summary>
		public override SessionStateStoreData GetItemExclusive(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actionFlags)
		{
			return GetSessionStoreItem(true, context, id, out locked, out lockAge, out lockId, out actionFlags);
		}

		/// <summary>
		/// GetSessionStoreItem is called by both the GetItem and
		/// GetItemExclusive methods. GetSessionStoreItem retrieves the </summary>
		/// session data from the data source. If the lockRecord parameter<param name="lockRecord"></param>
		/// is true (in the case of GetItemExclusive), then GetSessionStoreItem<param name="context"></param>
		/// locks the record and sets a new LockId and LockDate.<param name="id"></param>
		private SessionStateStoreData GetSessionStoreItem(bool lockRecord, HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actionFlags)
		{
			// Initial values for return value and out parameters.
			var state = new SQLiteSessionState(ApplicationName, _connectionString, _config)
				.GetState(lockRecord, id, context, this);

			locked = state.locked;
			lockAge = state.lockAge;
			lockId = state.lockId;
			actionFlags = state.actionFlags;

			return state.SessionStateStoreData;
		}

		/// <summary>
		/// Serialize is called by the SetAndReleaseItemExclusive method to
		/// convert the SessionStateItemCollection into a Base64 string
		private string Serialize(SessionStateItemCollection items)
		{
			MemoryStream ms = new MemoryStream();
			BinaryWriter writer = new BinaryWriter(ms);

			if (items != null)
				items.Serialize(writer);

			writer.Close();

			return Convert.ToBase64String(ms.ToArray());
		}

		/// <summary>
		/// SessionStateProviderBase.ReleaseItemExclusive
		/// </summary>
		public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
		{
			IDbConnection conn = new SQLiteConnection(_connectionString);
			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE Sessions SET Locked = 0, Expires = @Expires WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND LockId = @LockId";
			cmd.AddParameter("@Expires", DateTime.UtcNow.AddMinutes(_config.Timeout.TotalMinutes));
			cmd.AddParameter("@SessionId", id, 80);
			cmd.AddParameter("@ApplicationName", ApplicationName, 255);
			cmd.AddParameter("@LockId", (int)lockId);

			try
			{
				conn.Open();

				cmd.ExecuteNonQuery();
			}
			catch (SQLiteException e)
			{
				if (WriteExceptionsToEventLog)
				{
					e.WriteToEventLog("ReleaseItemExclusive");
					throw new ProviderException(_exceptionMessage);
				}
				else
					throw e;
			}
			finally
			{
				conn.Close();
			}
		}

		/// <summary>
		/// SessionStateProviderBase.RemoveItem
		/// </summary>
		public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
		{
			IDbConnection conn = new SQLiteConnection(_connectionString);
			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "DELETE FROM Sessions WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND LockId = @LockId";
			cmd.AddParameter("@SessionId", id, 80);
			cmd.AddParameter("@ApplicationName", ApplicationName, 255);
			cmd.AddParameter("@LockId", (int)lockId);

			try
			{
				conn.Open();

				cmd.ExecuteNonQuery();
			}
			catch (SQLiteException e)
			{
				if (WriteExceptionsToEventLog)
				{
					e.WriteToEventLog("RemoveItem");
					throw new ProviderException(_exceptionMessage);
				}
				else
					throw e;
			}
			finally
			{
				conn.Close();
			}
		}

		/// <summary>
		/// SessionStateProviderBase.CreateUninitializedItem
		/// </summary>
		public override void CreateUninitializedItem(HttpContext context, string id, int timeout)
		{
			IDbConnection conn = new SQLiteConnection(_connectionString);
			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "INSERT INTO Sessions (SessionId, ApplicationName, Created, Expires, LockDate, LockId, Timeout, Locked, SessionItems, Flags) Values(@SessionId, @ApplicationName, @Created, @Expires, @LockDate, @LockId, @Timeout, @Locked, @SessionItems, @Flags)";
			cmd.AddParameter("@SessionId", id, 80);
			cmd.AddParameter("@ApplicationName", ApplicationName);
			cmd.AddParameter("@Created", DateTime.UtcNow);
			cmd.AddParameter("@Expires", DateTime.UtcNow.AddMinutes((Double)timeout));
			cmd.AddParameter("@LockDate", DateTime.UtcNow);
			cmd.AddParameter("@LockId", 0);
			cmd.AddParameter("@Timeout", timeout);
			cmd.AddParameter("@Locked", false);
			cmd.AddParameter("@SessionItems", "", 0);
			cmd.AddParameter("@Flags", 1);

			try
			{
				conn.Open();

				cmd.ExecuteNonQuery();
			}
			catch (SQLiteException e)
			{
				if (WriteExceptionsToEventLog)
				{
					e.WriteToEventLog("CreateUninitializedItem");
					throw new ProviderException(_exceptionMessage);
				}
				else
					throw e;
			}
			finally
			{
				conn.Close();
			}
		}

		/// <summary>
		/// SessionStateProviderBase.CreateNewStoreData
		/// </summary>
		public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
		{
			return new SessionStateStoreData(new SessionStateItemCollection(), SessionStateUtility.GetSessionStaticObjects(context), timeout);
		}

		/// <summary>
		/// SessionStateProviderBase.ResetItemTimeout
		/// </summary>
		public override void ResetItemTimeout(HttpContext context, string id)
		{
			IDbConnection conn = new SQLiteConnection(_connectionString);
			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "UPDATE Sessions SET Expires = @Expires WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName";
			cmd.AddParameter("@Expires", DateTime.UtcNow.AddMinutes(_config.Timeout.TotalMinutes));
			cmd.AddParameter("@SessionId", id, 80);
			cmd.AddParameter("@ApplicationName", ApplicationName);

			try
			{
				conn.Open();

				cmd.ExecuteNonQuery();
			}
			catch (SQLiteException e)
			{
				if (WriteExceptionsToEventLog)
				{
					e.WriteToEventLog("ResetItemTimeout");
					throw new ProviderException(_exceptionMessage);
				}
				else
					throw e;
			}
			finally
			{
				conn.Close();
			}
		}

		/// <summary>
		/// SessionStateProviderBase.InitializeRequest
		/// </summary>
		public override void InitializeRequest(HttpContext context) { }

		/// <summary>
		/// SessionStateProviderBase.EndRequest
		/// </summary>
		public override void EndRequest(HttpContext context) { }

		/// <summary>
		/// Remove expired session data.
		/// </summary>
		private void CleanUpExpiredData()
		{
			IDbConnection conn = new SQLiteConnection(_connectionString);
			IDbCommand cmd = conn.CreateCommand();
			cmd.CommandText = "DELETE FROM Sessions WHERE Expires < @Expires";
			cmd.AddParameter("@Expires", DateTime.UtcNow);
			conn.Open();
			cmd.ExecuteNonQuery();
			conn.Close();
		}
	}
}