using System;
using System.Configuration.Provider;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Web;
using System.Web.Configuration;
using System.Web.SessionState;

namespace Littlefish.SQLiteSessionStateProvider
{
	internal class SQLiteSessionState
	{
		private SessionStateSection _config;

		private string _connectionString;
		public SessionStateStoreData SessionStateStoreData { get; private set; }
		private const string _exceptionMessage = "An exception occurred. Please contact your administrator.";
		public SessionStateActions actionFlags { get; private set; }

		private string serializedItems;

		private int timeout = 0;

		private DateTime expires;

		public TimeSpan lockAge { get; private set; }

		public int lockId { get; private set; }

		public string ApplicationName { get; private set; }

		public bool locked { get; private set; }

		public SQLiteSessionState(string ApplicationName, string connectionString, SessionStateSection config)
		{
			this.ApplicationName = ApplicationName;
			this._connectionString = connectionString;
			this._config = config;
			this.locked = false;
			this.lockId = 0;
			this.lockAge = TimeSpan.Zero;
			this.SessionStateStoreData = null;
			this.actionFlags = 0;
			this.serializedItems = "";

			// DateTime to check if current session item is expired.
			// Setting to expire a minute ago this will be reset if
			// session is valid
			this.expires = DateTime.Now.AddMinutes(-1);
		}

		public SQLiteSessionState GetState(bool lockRecord, string id, HttpContext context, SQLiteSessionStateStoreProvider provider)
		{
			// Timeout value from the data store.
			using (IDbConnection conn = new SQLiteConnection(_connectionString))
			{
				try
				{
					var state = new SessionInstance(conn, ApplicationName, id, lockRecord);

					if (state.HasExpired)
					{
						state.Locked = false;
						state.Delete();
					}
					else
					{
						// If the record was found and you obtained a lock, then set
						// the lockId, clear the actionFlags,
						// and create the SessionStateStoreItem to return.
						if (!state.Locked)
						{
							state.RenewLock();
							lockId = state.LockId;
							// If the actionFlags parameter is not InitializeItem,
							// deserialize the stored SessionStateItemCollection.
							if (actionFlags == SessionStateActions.InitializeItem)
								SessionStateStoreData = provider.CreateNewStoreData(context, (int)_config.Timeout.TotalMinutes);
							else
								SessionStateStoreData = Deserialize(context, serializedItems, timeout);
						}
					}
				}
				catch (SQLiteException e)
				{
					if (provider.WriteExceptionsToEventLog)
					{
						e.WriteToEventLog("GetSessionStoreItem");
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

			return this;
		}

		/// <summary>
		/// DeSerialize is called by the GetSessionStoreItem method to
		/// convert the Base64 string
		/// </summary>
		private SessionStateStoreData Deserialize(HttpContext context, string serializedItems, int timeout)
		{
			MemoryStream ms = new MemoryStream(Convert.FromBase64String(serializedItems));

			SessionStateItemCollection sessionItems =
			  new SessionStateItemCollection();

			if (ms.Length > 0)
			{
				BinaryReader reader = new BinaryReader(ms);
				sessionItems = SessionStateItemCollection.Deserialize(reader);
			}

			return new SessionStateStoreData(sessionItems,
			  SessionStateUtility.GetSessionStaticObjects(context),
			  timeout);
		}
	}
}