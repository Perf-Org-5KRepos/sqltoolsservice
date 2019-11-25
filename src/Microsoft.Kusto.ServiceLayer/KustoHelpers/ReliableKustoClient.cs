﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

// This code is copied from the source described in the comment below.

// =======================================================================================
// Microsoft Windows Server AppFabric Customer Advisory Team (CAT) Best Practices Series
//
// This sample is supplemental to the technical guidance published on the community
// blog at http://blogs.msdn.com/appfabriccat/ and  copied from
// sqlmain ./sql/manageability/mfx/common/
//
// =======================================================================================
// Copyright © 2012 Microsoft Corporation. All rights reserved.
// 
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND, EITHER 
// EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED WARRANTIES OF 
// MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE. YOU BEAR THE RISK OF USING IT.
// =======================================================================================

// namespace Microsoft.AppFabricCAT.Samples.Azure.TransientFaultHandling.SqlAzure
// namespace Microsoft.SqlServer.Management.Common

using System;
using System.Collections.Generic;
using System.Data;
using System.Collections;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Kusto.Data.Net.Client;
using Kusto.Data.Common;
using Kusto.Data;
using Microsoft.SqlTools.Utility;
using Microsoft.Kusto.ServiceLayer.KustoConstants;
using Microsoft.SqlTools.ServiceLayer.Connection.ReliableConnection;
using Microsoft.SqlServer.Management.Common;

namespace Microsoft.Kusto.ServiceLayer.Connection
{
    /// <summary>
    /// Provides a reliable way of opening connections to and executing commands
    /// taking into account potential network unreliability and a requirement for connection retry.
    /// </summary>
    public sealed partial class ReliableKustoClient : DbConnection, IDisposable
    {
        private readonly SqlConnection _underlyingConnection;
        private readonly ICslQueryProvider _client;
        private readonly RetryPolicy _connectionRetryPolicy;
        private RetryPolicy _commandRetryPolicy;
        private Guid _azureSessionId;

        /// <summary>
        /// Initializes a new instance of the ReliableKustoClient class with a given connection string
        /// and a policy defining whether to retry a request if the connection fails to be opened or a command
        /// fails to be successfully executed.
        /// </summary>
        /// <param name="connectionString">The connection string used to open the SQL Azure database.</param>
        /// <param name="connectionRetryPolicy">The retry policy defining whether to retry a request if a connection fails to be established.</param>
        /// <param name="commandRetryPolicy">The retry policy defining whether to retry a request if a command fails to be executed.</param>
        public ReliableKustoClient(string connectionString, RetryPolicy connectionRetryPolicy, RetryPolicy commandRetryPolicy, string azureAccountToken)
        {
            _client = CreateKustoClient(connectionString, azureAccountToken);
            
            //var query = ".show databases" + (clusterName.IndexOf(KustoHelperQueries.AriaProxyURL, StringComparison.CurrentCultureIgnoreCase) == -1 ? " | project DatabaseName, PrettyName" : "");
            //var reader = _client.ExecuteQuery(query);
            
            _underlyingConnection = new SqlConnection(connectionString);
            _connectionRetryPolicy = connectionRetryPolicy ?? RetryPolicyFactory.CreateNoRetryPolicy();
            _commandRetryPolicy = commandRetryPolicy ?? RetryPolicyFactory.CreateNoRetryPolicy();

            _underlyingConnection.StateChange += OnConnectionStateChange;
            _connectionRetryPolicy.RetryOccurred += RetryConnectionCallback;
            _commandRetryPolicy.RetryOccurred += RetryCommandCallback;

            if (azureAccountToken != null)
            {
                _underlyingConnection.AccessToken = azureAccountToken;
            }
        }

        public static ICslQueryProvider CreateKustoClient(string connectionString, string azureAccountToken)
        {
            var clusterName = GetClusterName(connectionString);
            var kcsb = new KustoConnectionStringBuilder(string.Format(
                            "{0};fed=true;UserToken={1}", 
                            clusterName, 
                            azureAccountToken));
            return KustoClientFactory.CreateCslQueryProvider(kcsb);
        }

        /*public static System.Data.IDataReader QueryKusto(
            Kusto.Data.Common.ICslQueryProvider queryProvider,
            string databaseName,
            string query)
        {
            var queryParameters = new Dictionary<String, String>()
            {
                { "xIntValue", "111" },
                { "xStrValue", "abc" },
                { "xDoubleValue", "11.1" }
            };

            // Query parameters (and many other properties) are provided
            // by a ClientRequestProperties object handed alongside
            // the query:
            var clientRequestProperties = new Kusto.Data.Common.ClientRequestProperties(
                principalIdentity: null,
                options: null,
                parameters: queryParameters);

            // Having client code provide its own ClientRequestId is
            // highly recommended. It not only allows the caller to
            // cancel the query, but also makes it possible for the Kusto
            // team to investigate query failures end-to-end:
            clientRequestProperties.ClientRequestId
                = "MyApp.MyActivity;"
                + Guid.NewGuid().ToString();

            // This is an example for setting an option
            // ("notruncation", in this case). In most cases this is not
            // needed, but it's included here for completeness:
            clientRequestProperties.SetOption(
                Kusto.Data.Common.ClientRequestProperties.OptionNoTruncation,
                true);
        
            try
            {
                return queryProvider.ExecuteQuery(query, clientRequestProperties);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "Failed invoking query '{0}' against Kusto."
                    + " To have the Kusto team investigate this failure,"
                    + " please open a ticket @ https://aka.ms/kustosupport,"
                    + " and provide: ClientRequestId={1}",
                    query, clientRequestProperties.ClientRequestId);
                return null;
            }
        }*/

        /// <summary>
        /// Extracts the cluster name from the connectionstring. The string looks like the following:
        /// "Data Source=clustername.kusto.windows.net;User ID=;Password=;Pooling=False;Application Name=azdata-GeneralConnection"
        /// <summary>
        /// <param name="dmpConnectionString">A connection string coming over the Data management protocol</param>
        static private string GetClusterName(string dmpConnectionString)
        {
            // TODOKusto: Unit test
            var csb = new SqlConnectionStringBuilder(dmpConnectionString);

            // If there is no https:// prefix add it
            Uri uri;
            if ((Uri.TryCreate(csb.DataSource, UriKind.Absolute, out uri) || Uri.TryCreate("https://" + csb.DataSource, UriKind.Absolute, out uri)) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.AbsoluteUri;
            }

            throw new ArgumentException("Expected a URL of the form clustername.kusto.windows.net");
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or
        ///  resetting managed and unmanaged resources.
        /// </summary>
        /// <param name="disposing">A flag indicating that managed resources must be released.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_connectionRetryPolicy != null)
                {
                    _connectionRetryPolicy.RetryOccurred -= RetryConnectionCallback;
                }

                if (_commandRetryPolicy != null)
                {
                    _commandRetryPolicy.RetryOccurred -= RetryCommandCallback;
                }

                _underlyingConnection.StateChange -= OnConnectionStateChange;
                if (_underlyingConnection.State == ConnectionState.Open)
                {
                    _underlyingConnection.Close();
                }

                _underlyingConnection.Dispose();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        internal static void SetLockAndCommandTimeout(IDbConnection conn)
        {
            Validate.IsNotNull(nameof(conn), conn);

            // Make sure we use the underlying connection as ReliableConnection.Open also calls
            // this method
            ReliableKustoClient reliableConn = conn as ReliableKustoClient;
            if (reliableConn != null)
            {
                conn = reliableConn._underlyingConnection;
            }

            const string setLockTimeout = @"set LOCK_TIMEOUT {0}";

            using (IDbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = string.Format(CultureInfo.InvariantCulture, setLockTimeout, AmbientSettings.LockTimeoutMilliSeconds);
                cmd.CommandType = CommandType.Text;
                cmd.CommandTimeout = CachedServerInfo.Instance.GetQueryTimeoutSeconds(conn);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Gets or sets the connection string for opening a connection to the SQL Azure database.
        /// </summary>
        public override string ConnectionString
        {
            get { return _underlyingConnection.ConnectionString; }
            set { _underlyingConnection.ConnectionString = value; }
        }

        /// <summary>
        /// Gets the policy which decides whether to retry a connection request, based on how many
        /// times the request has been made and the reason for the last failure. 
        /// </summary>
        public RetryPolicy ConnectionRetryPolicy
        {
            get { return _connectionRetryPolicy; }
        }

        /// <summary>
        /// Gets the policy which decides whether to retry a command, based on how many
        /// times the request has been made and the reason for the last failure. 
        /// </summary>
        public RetryPolicy CommandRetryPolicy
        {
            get { return _commandRetryPolicy; }
            set
            {
                Validate.IsNotNull(nameof(value), value);

                if (_commandRetryPolicy != null)
                {
                    _commandRetryPolicy.RetryOccurred -= RetryCommandCallback;
                }

                _commandRetryPolicy = value;
                _commandRetryPolicy.RetryOccurred += RetryCommandCallback;
            }
        }

        /// <summary>
        /// Gets the server name from the underlying connection.
        /// </summary>
        public override string DataSource
        {
            get { return _underlyingConnection.DataSource; }
        }

        /// <summary>
        /// Gets the server version from the underlying connection.
        /// </summary>
        public override string ServerVersion
        {
            get { return _underlyingConnection.ServerVersion; }
        }

        /// <summary>
        /// If the underlying SqlConnection absolutely has to be accessed, for instance
        /// to pass to external APIs that require this type of connection, then this
        /// can be used.  
        /// </summary>
        /// <returns><see cref="SqlConnection"/></returns>
        public SqlConnection GetUnderlyingConnection()
        {
            return _underlyingConnection;
        }

        /// <summary>
        /// Begins a database transaction with the specified System.Data.IsolationLevel value.
        /// </summary>
        /// <param name="level">One of the System.Data.IsolationLevel values.</param>
        /// <returns>An object representing the new transaction.</returns>
        protected override DbTransaction BeginDbTransaction(IsolationLevel level)
        {
            return _underlyingConnection.BeginTransaction(level);
        }

        /// <summary>
        /// Changes the current database for an open Connection object.
        /// </summary>
        /// <param name="databaseName">The name of the database to use in place of the current database.</param>
        public override void ChangeDatabase(string databaseName)
        {
            _underlyingConnection.ChangeDatabase(databaseName);
        }

        /// <summary>
        /// Opens a database connection with the settings specified by the ConnectionString
        /// property of the provider-specific Connection object.
        /// </summary>
        public override void Open()
        {
            // Check if retry policy was specified, if not, disable retries by executing the Open method using RetryPolicy.NoRetry.
            _connectionRetryPolicy.ExecuteAction(() =>
            {
                if (_underlyingConnection.State != ConnectionState.Open)
                {
                    _underlyingConnection.Open();
                }
                SetLockAndCommandTimeout(_underlyingConnection);
            });
        }

        /// <summary>
        /// Opens a database connection with the settings specified by the ConnectionString
        /// property of the provider-specific Connection object.
        /// </summary>
        public override Task OpenAsync(CancellationToken token)
        {
            // Make sure that the token isn't cancelled before we try
            if (token.IsCancellationRequested)
            {
                return Task.FromCanceled(token);
            }

            // Check if retry policy was specified, if not, disable retries by executing the Open method using RetryPolicy.NoRetry.
            try
            {
                return _connectionRetryPolicy.ExecuteAction(async () =>
                {
                    if (_underlyingConnection.State != ConnectionState.Open)
                    {
                        await _underlyingConnection.OpenAsync(token);
                    }
                    SetLockAndCommandTimeout(_underlyingConnection);
                });
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        /// <summary>
        /// Closes the connection to the database.
        /// </summary>
        public override void Close()
        {
            _underlyingConnection.Close();
        }

        /// <summary>
        /// Gets the time to wait while trying to establish a connection before terminating
        /// the attempt and generating an error.
        /// </summary>
        public override int ConnectionTimeout
        {
            get { return _underlyingConnection.ConnectionTimeout; }
        }

        /// <summary>
        /// Creates and returns an object implementing the IDbCommand interface which is associated 
        /// with the underlying SqlConnection.
        /// </summary>
        /// <returns>A <see cref="IDbCommand"/> object.</returns>
        protected override DbCommand CreateDbCommand()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Creates and returns an object implementing the IDbCommand interface which is associated 
        /// with the underlying SqlConnection.
        /// </summary>
        /// <returns>A <see cref="SqlCommand"/> object.</returns>
        public SqlCommand CreateSqlCommand()
        {
            return _underlyingConnection.CreateCommand();
        }

        /// <summary>
        /// Gets the name of the current database or the database to be used after a
        /// connection is opened.
        /// </summary>
        public override string Database
        {
            get { return _underlyingConnection.Database; }
        }

        /// <summary>
        /// Gets the current state of the connection.
        /// </summary>
        public override ConnectionState State
        {
            get { return _underlyingConnection.State; }
        }

        /// <summary>
        /// Adds an info message event Listener.
        /// </summary>
        /// <param name="handler">An info message event Listener.</param>
        public void AddInfoMessageHandler(SqlInfoMessageEventHandler handler)
        {
            _underlyingConnection.InfoMessage += handler;
        }

        /// <summary>
        /// Removes an info message event Listener.
        /// </summary>
        /// <param name="handler">An info message event Listener.</param>
        public void RemoveInfoMessageHandler(SqlInfoMessageEventHandler handler)
        {
            _underlyingConnection.InfoMessage -= handler;
        }

        /// <summary>
        /// Clears underlying connection pool.
        /// </summary>
        public void ClearPool()
        {
            if (_underlyingConnection != null)
            {
                SqlConnection.ClearPool(_underlyingConnection);
            }
        }

        private void RetryCommandCallback(RetryState retryState)
        {
            
        }

        private void RetryConnectionCallback(RetryState retryState)
        {
            
        }

        public void OnConnectionStateChange(object sender, StateChangeEventArgs e)
        {
            SqlConnection conn = (SqlConnection)sender;
            switch (e.CurrentState)
            {
                case ConnectionState.Open:
                case ConnectionState.Broken:
                case ConnectionState.Closed:
                    _azureSessionId = Guid.Empty;
                    break;
                case ConnectionState.Connecting:
                case ConnectionState.Executing:
                case ConnectionState.Fetching:
                default:
                    break;
            }
        }

        private void VerifyConnectionOpen(IDbCommand command)
        {
            // Verify whether or not the connection is valid and is open. This code may be retried therefore
            // it is important to ensure that a connection is re-established should it have previously failed.
            if (command.Connection == null)
            {
                command.Connection = this;
            }

            if (command.Connection.State != ConnectionState.Open)
            {
                SqlConnection.ClearPool(_underlyingConnection);

                command.Connection.Open();
            }
        }

        private IDataReader ExecuteReader(IDbCommand command, CommandBehavior behavior)
        {
            Tuple<string, bool>[] sessionSettings = null;
            return _commandRetryPolicy.ExecuteAction<IDataReader>(() =>
            {
                VerifyConnectionOpen(command);
                sessionSettings = CacheOrReplaySessionSettings(command, sessionSettings);

                return command.ExecuteReader(behavior);
            });
        }

        // Because retry loses session settings, cache session settings or reply if the settings are already cached.
        public Tuple<string, bool>[] CacheOrReplaySessionSettings(IDbCommand originalCommand, Tuple<string, bool>[] sessionSettings)
        {
            if (sessionSettings == null)
            {
                sessionSettings = QuerySessionSettings(originalCommand);
            }
            else
            {
                SetSessionSettings(originalCommand.Connection, sessionSettings);
            }

            return sessionSettings;
        }

        private object ExecuteScalar(IDbCommand command)
        {
            Tuple<string,bool>[] sessionSettings = null;
            return _commandRetryPolicy.ExecuteAction(() =>
            {
                VerifyConnectionOpen(command);
                sessionSettings = CacheOrReplaySessionSettings(command, sessionSettings);

                return command.ExecuteScalar();
            });
        }

        private Tuple<string, bool>[] QuerySessionSettings(IDbCommand originalCommand)
        {
            Tuple<string,bool>[] sessionSettings = new Tuple<string,bool>[2];

            IDbConnection connection = originalCommand.Connection;
            
            using (IDbCommand localCommand = connection.CreateCommand())
            {
                // Executing a reader requires preservation of any pending transaction created by the calling command
                localCommand.Transaction = originalCommand.Transaction;
                localCommand.CommandText = "SELECT ISNULL(SESSIONPROPERTY ('ANSI_NULLS'), 0), ISNULL(SESSIONPROPERTY ('QUOTED_IDENTIFIER'), 1)";
                using (IDataReader reader = localCommand.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        sessionSettings[0] = Tuple.Create("ANSI_NULLS", ((int)reader[0] == 1));
                        sessionSettings[1] = Tuple.Create("QUOTED_IDENTIFIER", ((int)reader[1] ==1));
                    }
                    else
                    {
                        Debug.Assert(false, "Reader cannot be empty");
                    }
                }
            }
            return sessionSettings;
        }

        private void SetSessionSettings(IDbConnection connection, params  Tuple<string, bool>[] settings)
        {
            List<string> setONOptions = new List<string>();
            List<string> setOFFOptions = new List<string>();
            if(settings != null)
            {
                foreach (Tuple<string, bool> setting in settings)
                {
                    if (setting.Item2)
                    {
                        setONOptions.Add(setting.Item1);
                    }
                    else
                    {
                        setOFFOptions.Add(setting.Item1);
                    }
                }
            }

            SetSessionSettings(connection, setONOptions, "ON");
            SetSessionSettings(connection, setOFFOptions, "OFF");

        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        private static void SetSessionSettings(IDbConnection connection, List<string> sessionOptions, string onOff)
        {
            if (sessionOptions.Count > 0)
            {
                using (IDbCommand localCommand = connection.CreateCommand())
                {
                    StringBuilder builder = new StringBuilder("SET ");
                    for (int i = 0; i < sessionOptions.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }
                        builder.Append(sessionOptions[i]);
                    }
                    builder.Append(" ");
                    builder.Append(onOff);
                    localCommand.CommandText = builder.ToString();
                    localCommand.ExecuteNonQuery();
                }
            }
        }

        private int ExecuteNonQuery(IDbCommand command)
        {
            Tuple<string, bool>[] sessionSettings = null;
            return _commandRetryPolicy.ExecuteAction<int>(() =>
            {
                VerifyConnectionOpen(command);
                sessionSettings = CacheOrReplaySessionSettings(command, sessionSettings);

                return command.ExecuteNonQuery();
            });
        }
    }
}
