﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Security;
using System.Xml;
using System.Xml.Serialization;
using SolarWinds.InformationService.Contract2;
using SolarWinds.InformationService.Contract2.PubSub;
using SolarWinds.InformationService.InformationServiceClient;
using SwqlStudio.Properties;

namespace SwqlStudio
{
    public class ConnectionInfo : IDisposable
    {
        public string ServerType { get; set; }
        private string _server;
        private string _username;
        private string _password;
        private string _activeSubscriberAddress;

        private InfoServiceProxy _proxy;
        private readonly InfoServiceBase _infoServiceType;

        private NotificationDeliveryServiceProxy _notificationDeliveryServiceProxy;
        private SubscriberInfo _activeSubscriberInfo;

        public ConnectionInfo(string server, string username, string password, string serverType)
        {
            ServerType = serverType;
            _server = server;
            _username = username;
            _password = password;

            _infoServiceType = InfoServiceFactory.Create(serverType, username, password);
            QueryParameters = new PropertyBag();
        }

        public string Server
        {
            get { return _server; }
            set { _server = value; }
        }

        public string UserName
        {
            get { return _username; }
            set { _username = value; }
        }

        public string Password
        {
            get { return _password; }
            set { _password = value; }
        }

        public bool CanCreateSubscription { get; set; }

        public string Title
        {
            get
            {
                return String.Format("{0} : {1} [{2}]", Server, ServerType, UserName);
            }
        }

        public InfoServiceProxy Proxy
        {
            get { return _proxy; }
        }

        public InformationServiceConnection Connection { get; private set; }

        public static List<ServerType> AvailableServerTypes
        {
            get
            {
                List<ServerType> serverTypes = new List<ServerType>
                {
                    new ServerType() { Type = "Orion (v3)", IsAuthenticationRequired = true },
                    new ServerType() { Type = "Orion (v3) AD", IsAuthenticationRequired = false },
                    new ServerType() { Type = "Orion (v3) Certificate", IsAuthenticationRequired = false },
                    new ServerType() { Type = "Orion (v3) over HTTPS", IsAuthenticationRequired = true },
                    new ServerType() { Type = "Orion (v2)", IsAuthenticationRequired = true },
                    new ServerType() { Type = "Orion (v2) AD", IsAuthenticationRequired = false },
                    new ServerType() { Type = "Orion (v2) Certificate", IsAuthenticationRequired = false },
                    new ServerType() { Type = "Orion (v2) over HTTPS", IsAuthenticationRequired = true },
                    new ServerType() { Type = "EOC", IsAuthenticationRequired = true },
                    new ServerType() { Type = "NCM", IsAuthenticationRequired = true },
                    new ServerType() { Type = "NCM (Windows Authentication)", IsAuthenticationRequired = false },
                    new ServerType() { Type = "NCM Integration", IsAuthenticationRequired = true },
                    new ServerType() { Type = "Java over HTTP", IsAuthenticationRequired = true }
                };

                if (Settings.Default.ShowCompressedModes)
                {
                    serverTypes.AddRange(new[]
                                        {
                                            new ServerType() { Type = "Orion (v2) Compressed", IsAuthenticationRequired = true },
                                            new ServerType() { Type = "Orion (v2) AD Compressed", IsAuthenticationRequired = false },
                                            new ServerType() { Type = "Orion (v3) Compressed", IsAuthenticationRequired = true },
                                            new ServerType() { Type = "Orion (v3) AD Compressed", IsAuthenticationRequired = false },                        
                                        });

                }

                return serverTypes;

            }
        }

        public PropertyBag QueryParameters { get; set; }
        public INotificationSubscriber NotificationSubscriber { get; set; }

        public void Connect()
        {
            if (_proxy == null || (_proxy != null && (_proxy.Channel.State == CommunicationState.Closed || _proxy.Channel.State == CommunicationState.Faulted)))
            {
                if(_proxy != null)
                    _proxy.Dispose();

                _proxy = _infoServiceType.CreateProxy(_server);
                _proxy.OperationTimeout = TimeSpan.FromMinutes(Settings.Default.OperationTimeout);
                _proxy.ChannelFactory.Endpoint.Behaviors.Add(new LogHeaderReaderBehavior());
                _proxy.Open();
            }

            Connection = new InformationServiceConnection((IInformationService)_proxy);
            Connection.Open();

            if (Settings.Default.UseActiveSubscriber && _infoServiceType.SupportsActiveSubscriber)
            {
                _notificationDeliveryServiceProxy = _infoServiceType.CreateNotificationDeliveryServiceProxy(_server, NotificationSubscriber);
                _notificationDeliveryServiceProxy.Open();
                _activeSubscriberAddress = string.Format("active://{0}/SolarWinds/SwqlStudio/{1}", Utility.GetFqdn(), Process.GetCurrentProcess().Id);
                _notificationDeliveryServiceProxy.ReceiveIndications(_activeSubscriberAddress);
                _activeSubscriberInfo = new SubscriberInfo()
                {
                    EndpointAddress = _activeSubscriberAddress,
                    OpenedSuccessfully = true,
                    DataFormat = "Xml"
                };
            }
        }

        public virtual SubscriberInfo GetActiveSubscriberInfo()
        {
            return _activeSubscriberInfo;
        }

        private void EnsureConnection()
        {
            if ((Connection != null) && (Connection.State != ConnectionState.Open))
                Connect();
        }

        public IEnumerable<T> Query<T>(string swql) where T: new()
        {
            EnsureConnection();

            using(var context = new InformationServiceContext( _proxy))
            using(var serviceQuery = context.CreateQuery<T>(swql))
            {
                var enumerator = serviceQuery.GetEnumerator();
                while(enumerator.MoveNext())
                {
                    yield return enumerator.Current;
                }
            }
        }

        public DataTable Query(string swql)
        {
            XmlDocument dummy;
            return Query(swql, out dummy);
        }

        public DataTable Query(string swql, out XmlDocument queryPlan)
        {
            EnsureConnection();

            XmlDocument tmpQueryPlan = null; // can't reference out parameter from closure

            DataTable result = DoWithExceptionTranslation(
                delegate
                    {
                        using (InformationServiceCommand command = new InformationServiceCommand(swql, Connection) {ApplicationTag = "SWQL Studio"})
                        {
                            foreach (var param in QueryParameters)
                                command.Parameters.AddWithValue(param.Key, param.Value);

                            InformationServiceDataAdapter dataAdapter = new InformationServiceDataAdapter(command);
                            DataTable resultDataTable = new DataTable();
                            dataAdapter.Fill(resultDataTable);

                            tmpQueryPlan = dataAdapter.QueryPlan;
                            return resultDataTable;
                        }
                    });

            queryPlan = tmpQueryPlan;
            return result;
        }

        public static void DoWithExceptionTranslation(Action action)
        {
            DoWithExceptionTranslation(delegate
                                           {
                                               action();
                                               return 0;
                                           });
        }

        public static T DoWithExceptionTranslation<T>(Func<T> action)
        {
            string msg;
            Exception inner;

            try
            {
                return action();
            }
            catch (FaultException<InfoServiceFaultContract> ex)
            {
                msg = ex.Detail.Message;
                inner = ex;
            }
            catch (SecurityNegotiationException ex)
            {
                msg = ex.Message;
                inner = ex;
            }
            catch (FaultException ex)
            {
                msg = (ex.InnerException != null) ? ex.InnerException.Message : ex.Message;
                inner = ex.InnerException ?? ex;
            }
            catch (MessageSecurityException ex)
            {
                if (ex.InnerException != null && ex.InnerException is FaultException)
                {
                    msg = (ex.InnerException as FaultException).Message;
                    inner = ex.InnerException;
                }
                else
                {
                    msg = ex.Message;
                    inner = ex;
                }
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                inner = ex;
            }

            throw new ApplicationException(msg, inner);
        }

        public XmlDocument QueryXml(string query, out XmlDocument queryPlan, out List<ErrorMessage> errorMessages)
        {
            EnsureConnection();
            Message results;
            errorMessages = null;

            using (new SwisSettingsContext { DataProviderTimeout = TimeSpan.FromSeconds(30), ApplicationTag = "SWQL Studio", AppendErrors = true})
            {
                results = _proxy.Query(new QueryXmlRequest(query, QueryParameters));
            }
            
            XmlReader reader = results.GetReaderAtBodyContents();
            var body = new XmlDocument(reader.NameTable);
            body.Load(reader);

            var nsmgr = new XmlNamespaceManager(reader.NameTable);
            nsmgr.AddNamespace("is", Constants.Namespace);

            bool hasErrors = false;
            if (results.Headers.FindHeader("hasErrors", Constants.Namespace) > -1)
            {
                hasErrors = results.Headers.GetHeader<bool>("hasErrors", Constants.Namespace);
            }

            if (hasErrors)
            {
                XmlNode errorsNode = body.SelectSingleNode("/is:QueryXmlResponse/is:QueryXmlResult/errors", nsmgr);

                if (errorsNode != null)
                {
                    errorMessages = new List<ErrorMessage>();

                    foreach (XmlNode node in errorsNode.ChildNodes)
                    {
                        XmlSerializer serializer = new XmlSerializer(typeof(ErrorMessage));
                        ErrorMessage message = (ErrorMessage)serializer.Deserialize(new StringReader(node.OuterXml));

                        if (message != null)
                            errorMessages.Add(message);
                    }

                    errorsNode.ParentNode.RemoveChild(errorsNode);
                }
            }

            // Extract query plan if present
            XmlNode queryPlanNode = body.SelectSingleNode("/is:QueryXmlResponse/is:QueryXmlResult/is:queryResult/is:queryPlan", nsmgr);
            if (queryPlanNode != null)
            {
                queryPlan = new XmlDocument();
                queryPlan.LoadXml(queryPlanNode.OuterXml);
                queryPlanNode.ParentNode.RemoveChild(queryPlanNode);
            }
            else
            {
                queryPlan = null;
            }
            
            return body;
        }

        public void Dispose()
        {
            Close();
        }

        public void Close()
        {
            if (_proxy != null)
            {
                _proxy.Dispose();
                _proxy = null;
            }
        }

        internal ConnectionInfo Copy()
        {
            return new ConnectionInfo(_server, _username, _password, _infoServiceType.ServiceType)
                       {
                           QueryParameters = QueryParameters
                       };
        }
    }
}
