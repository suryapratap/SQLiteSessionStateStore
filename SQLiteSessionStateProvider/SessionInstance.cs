using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.SessionState;

namespace Littlefish.SQLiteSessionStateProvider
{
	internal class SessionInstance : IDisposable
	{
		private IDbConnection conn;
		private bool isLocked;

		public string SessionId { get; set; }
		public string ApplicationName { get; private set; }
		public DateTime Created { get; set; }
		public DateTime Expires { get; set; }
		public DateTime LockDate { get; set; }
		public int LockId { get; set; }
		public int Timeout { get; set; }
		public bool Locked { get; set; }
		public string SessionItems { get; set; }
		public int Flags { get; set; }

		public SessionStateActions ActionFlags { get; private set; }
		public TimeSpan LockAge { get; private set; }

		public bool HasExpired { get { return Expires < DateTime.UtcNow; } }

		public bool Loaded { get; private set; }

		public SessionInstance(IDbConnection conn, string applicationName, string id = null)
			: this(conn, applicationName, id, false)
		{
		}

		public SessionInstance(IDbConnection conn, string applicationName, string id, bool lockRecord)
		{
			this.conn = conn;
			this.ApplicationName = applicationName;
			if (id != null)
			{
				this.SessionId = id;
				LoadCurrent(lockRecord);
			}
		}

		private void OpenConnection()
		{
			if (conn.State != ConnectionState.Open)
				conn.Open();
		}

		public void Delete(bool checkExpiry = false)
		{
			OpenConnection();
			using (IDbCommand cmd = conn.CreateCommand())
			{
				cmd.AddParameter("@SessionId", SessionId, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName);

				if (checkExpiry)
				{
					cmd.CommandText = "DELETE FROM Sessions WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND Expires < @Expires";
					cmd.AddParameter("@Expires", DateTime.UtcNow);
				}
				else
				{
					cmd.CommandText = "DELETE FROM Sessions WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName ";
				}

				cmd.ExecuteNonQuery();
			}
		}

		public void RenewLock()
		{
			LockId = LockId + 1;
			using (IDbCommand cmd = conn.CreateCommand())
			{
				cmd.CommandText = "UPDATE Sessions SET LockId = @LockId, Flags = 0 WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName";
				cmd.AddParameter("@LockId", LockId);
				cmd.AddParameter("@SessionId", SessionId, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName, 255);

				cmd.ExecuteNonQuery();
			}
		}

		public void Insert()
		{
			OpenConnection();
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = "INSERT INTO Sessions (SessionId, ApplicationName, Created, Expires, LockDate, LockId, Timeout, Locked, SessionItems, Flags)" +
								  " Values(@SessionId, @ApplicationName, @Created, @Expires, @LockDate, @LockId, @Timeout, @Locked, @SessionItems, @Flags)";

				cmd.AddParameter("@SessionId", SessionId, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName, 255);
				cmd.AddParameter("@Created", Created);
				cmd.AddParameter("@Expires", Expires);
				cmd.AddParameter("@LockDate", LockDate);
				cmd.AddParameter("@LockId", LockId);
				cmd.AddParameter("@Timeout", Timeout);
				cmd.AddParameter("@Locked", Locked);
				cmd.AddParameter("@SessionItems", SessionItems, SessionItems.Length);
				cmd.AddParameter("@Flags", 0);
				cmd.ExecuteNonQuery();
			}
		}

		/// <summary>
		/// SQLiteCommand to update the existing session item.
		/// </summary>
		public void Set()
		{
			OpenConnection();
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = "UPDATE Sessions SET Expires = @Expires, SessionItems = @SessionItems, Locked = @Locked WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND LockId = @LockId";
				cmd.AddParameter("@Expires", DateTime.UtcNow.AddMinutes((Double)Timeout));
				cmd.AddParameter("@SessionItems", SessionItems, SessionItems.Length);
				cmd.AddParameter("@Locked", false);
				cmd.AddParameter("@SessionId", SessionId, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName);
				cmd.AddParameter("@LockId", LockId);
			}
		}

		private void LoadCurrent(bool setLock)
		{
			OpenConnection();
			if (setLock)
			{
				using (IDbCommand cmd = conn.CreateCommand())
				{
					cmd.CommandText = "UPDATE Sessions SET Locked = @Locked1, LockDate = @LockDate WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND Locked = @Locked2 AND Expires > @Expires";
					cmd.AddParameter("@Locked1", true);
					cmd.AddParameter("@LockDate", DateTime.UtcNow);
					cmd.AddParameter("@SessionId", SessionId, 80);
					cmd.AddParameter("@ApplicationName", ApplicationName);
					cmd.AddParameter("@Locked2", false);
					cmd.AddParameter("@Expires", DateTime.UtcNow);

					// if no record was updated because the record was locked or not found.
					// set locked = true
					isLocked = cmd.ExecuteNonQuery() == 0;
				}
			}

			using (IDbCommand cmd = conn.CreateCommand())
			{
				cmd.CommandText = "SELECT Expires, SessionItems, LockId, LockDate, Flags, Timeout FROM Sessions WHERE SessionId = @SessionId AND ApplicationName = @ApplicationName AND";
				cmd.AddParameter("@SessionId", SessionId, 80);
				cmd.AddParameter("@ApplicationName", ApplicationName);
				this.Loaded = false;
				// Retrieve session item data from the data source.
				using (IDataReader reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
				{
					while (reader.Read())
					{
						Expires = reader.GetDateTime(0);
						SessionItems = reader.GetString(1);
						LockId = reader.GetInt32(2);
						LockDate = reader.GetDateTime(3);
						Flags = reader.GetInt32(4);
						Timeout = reader.GetInt32(5);
						ActionFlags = (SessionStateActions)Flags;
						LockAge = DateTime.UtcNow.Subtract(LockDate);
						this.Loaded = true;
					}
					reader.Close();
				}
			}
		}

		/// <summary>
		/// DeSerialize is called by the GetSessionStoreItem method to
		/// convert the Base64 string
		/// </summary>
		public SessionStateStoreData Deserialize(HttpContext context)
		{
			MemoryStream ms = new MemoryStream(Convert.FromBase64String(SessionItems));

			SessionStateItemCollection sessionItems =
			  new SessionStateItemCollection();

			if (ms.Length > 0)
			{
				BinaryReader reader = new BinaryReader(ms);
				sessionItems = SessionStateItemCollection.Deserialize(reader);
			}

			return new SessionStateStoreData(sessionItems,
			  SessionStateUtility.GetSessionStaticObjects(context),
			  Timeout);
		}

		public void Dispose()
		{
			if (conn.State == ConnectionState.Open)
			{
				conn.Close();
			}
		}
	}
}