using System;
using System.Data;
using System.Data.SQLite;

namespace Littlefish.SQLiteSessionStateProvider
{
	internal static class SQLiteHelper
	{
		public static IDbDataParameter CreateParameter(string name, DbType type, object value)
		{
			return new SQLiteParameter
			{
				ParameterName = name,
				DbType = type,
				Value = value
			};
		}

		public static IDbDataParameter CreateParameter(string name, DbType type, int size, object value)
		{
			return new SQLiteParameter
			{
				ParameterName = name,
				DbType = type,
				Size = size,
				Value = value
			};
		}

		public static int AddParameter(this IDbCommand cmd, string parameterName, int value)
		{
			var param = CreateParameter(parameterName, DbType.Int32, value);
			return cmd.Parameters.Add(param);
		}

		public static int AddParameter(this IDbCommand cmd, string parameterName, bool value)
		{
			var param = CreateParameter(parameterName, DbType.Boolean, value);
			return cmd.Parameters.Add(param);
		}

		public static int AddParameter(this IDbCommand cmd, string parameterName, DateTime value)
		{
			var param = CreateParameter(parameterName, DbType.DateTime, value);
			return cmd.Parameters.Add(param);
		}

		public static int AddParameter(this IDbCommand cmd, string parameterName, string value)
		{
			var param = CreateParameter(parameterName, DbType.String, value);
			return cmd.Parameters.Add(param);
		}

		public static int AddParameter(this IDbCommand cmd, string parameterName, string value, int length)
		{
			var param = CreateParameter(parameterName, DbType.String, length, value);
			return cmd.Parameters.Add(param);
		}
	}
}