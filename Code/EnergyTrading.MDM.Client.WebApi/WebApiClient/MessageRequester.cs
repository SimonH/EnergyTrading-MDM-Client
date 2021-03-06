﻿namespace EnergyTrading.Mdm.Client.WebApi.WebApiClient
{
    using EnergyTrading;
    using EnergyTrading.Contracts.Search;
    using EnergyTrading.Extensions;
    using EnergyTrading.Logging;
    using EnergyTrading.Mdm.Client.Constants;
    using EnergyTrading.Mdm.Client.Extensions;
    using EnergyTrading.Mdm.Client.WebClient;
    using EnergyTrading.Mdm.Contracts;
    using Microsoft.Practices.Unity;
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Formatting;
    using System.Reflection;
    using System.ServiceModel.Syndication;
    using System.Xml;

    /// <summary>
    /// Implements a IMessageRequester using IHttpClientFactory.
    /// </summary>
    public class MessageRequester : IMessageRequester
    {
        private readonly static ILogger Logger = LoggerFactory.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IHttpClientFactory httpClientFactory;
        private readonly IFaultHandler faultHandler;

        // Used whilst we agree on this approach and then we can get rid of this constructor and each client can choose there own fault handling
        [InjectionConstructor]
        public MessageRequester(IHttpClientFactory httpClientFactory)
            : this(httpClientFactory, new StandardFaultHandler())
        {
        }

        public MessageRequester(IHttpClientFactory httpClientFactory, IFaultHandler faultHandler)
        {
            this.httpClientFactory = httpClientFactory;
            this.faultHandler = faultHandler;
        }

        /// <copydocfrom cref="IMessageRequester.Create{T}" />
        public WebResponse<TMessage> Create<TMessage>(string uri, TMessage message)
        {
            return Create(uri, message, null);
        }

        /// <copydocfrom cref="IMessageRequester.Delete{T}" />
        public WebResponse<TMessage> Delete<TMessage>(string uri)
        {
            return Delete<TMessage>(uri, null);
        }

        /// <copydocfrom cref="IMessageRequester.Request{T}" />
        public WebResponse<TMessage> Request<TMessage>(string uri)
        {
            Logger.DebugFormat("Start: MessageRequester.Request {0}", uri);
            var webResponse = new WebResponse<TMessage>();

            this.ResponseHandler(webResponse, uri, client =>
            {
                client.AddHeader(CoreConstants.UserNameHeader, GetUserName());
                using (var response = client.Get(uri))
                {
                    this.PopulateWebResponse(webResponse, response, HttpStatusCode.OK);

                    if (webResponse.IsValid)
                    {
                        // Ok, get the other bits that should be there.
                        webResponse.Tag = (response.Headers.ETag == null) ? null : response.Headers.ETag.Tag;
                        webResponse.Message = response.Content.ReadAsAsync<TMessage>().Result;
                    }
                }
            });

            Logger.DebugFormat("Finish: MessageRequester.Request  {0}", uri);
            return webResponse;
        }

        /// <copydocfrom cref="IMessageRequester.Search{T}" />
        public PagedWebResponse<IList<TContract>> Search<TContract>(string uri, Search message)
        {
            Logger.DebugFormat("Start: MessageRequester.Search {0}", uri);

            var result = new PagedWebResponse<IList<TContract>>
            {
                Message = new List<TContract>(0)
            };

            this.ResponseHandler(
                result,
                uri,
                client =>
                {
                    client.AddHeader(CoreConstants.UserNameHeader, GetUserName());
                    var content = new ObjectContent<Search>(message, new XmlMediaTypeFormatter());
                    using (var response = client.Post(uri, content))
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            Logger.Debug("MessageRequester.Search : Status OK - Reading the content from Response");
                            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                            Stream stream = null;
                            var readTask = response.Content.ReadAsStreamAsync().ContinueWith(task =>
                                {
                                    if (task.Exception != null)
                                    {
                                        Logger.DebugFormat("MessageRequester.Search : Exception occurred while reading content asynchronously - {0}", task.Exception.Message);
                                        result.Code = HttpStatusCode.InternalServerError;
                                        result.IsValid = false;
                                        result.Fault = new Fault { Message = "Exception reading response stream : " + task.Exception.Message };
                                    }
                                    else
                                    {
                                        stream = task.Result;
                                    }
                                    return task;
                                });
                            readTask.Wait();
                            if (stream != null)
                            {
                                var reader = XmlReader.Create(stream, settings);

                                var feed = SyndicationFeed.Load(reader);
                                if (feed == null)
                                {
                                    result.Code = HttpStatusCode.InternalServerError;
                                }
                                else
                                {
                                    Logger.Debug("MessageRequester.Search : Populating Message with the response received");
                                    result.Message = feed.Items.Select(syndicationItem => (XmlSyndicationContent)syndicationItem.Content).Select(syndic => syndic.ReadContent<TContract>()).ToList();
                                    result.Code = HttpStatusCode.OK;

                                    var nextPageLink = feed.Links.FirstOrDefault(syndicationLink => syndicationLink.RelationshipType == "next-results");
                                    result.NextPage = nextPageLink == null ? null : nextPageLink.Uri;
                                }
                            }
                        }
                        else
                        {
                            result.Code = response.StatusCode;

                            if (response.StatusCode == HttpStatusCode.NotFound)
                            {
                                result.IsValid = false;
                                result.Message = new List<TContract>();
                                return;
                            }

                            result.IsValid = false;
                            result.Fault = this.GetFault(response);
                        }
                    }
                });

            Logger.DebugFormat("Finish: MessageRequester.Search {0}", uri);
            return result;
        }

        /// <copydocfrom cref="IMessageRequester.Update{T}" />
        public WebResponse<TMessage> Update<TMessage>(string uri, string etag, TMessage message)
        {
            return Update(uri, etag, message, null);
        }

        private void ResponseHandler<T>(WebResponse<T> response, string uri, Action<IHttpClient> action)
        {
            try
            {
                using (var client = this.httpClientFactory.Create(uri))
                {
                    action(client);
                    client.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.AllExceptionMessages());
                response.Code = HttpStatusCode.InternalServerError;
                response.IsValid = false;
                response.Fault = new Fault { Message = ex.AllExceptionMessages() };
            }
        }

        private void PopulateWebResponse<T>(WebResponse<T> webResponse, HttpResponseMessage httpResponse, HttpStatusCode validCode)
        {
            Logger.DebugFormat("MessageRequester.PopulateWebResponse<{0}> - Populating Web reponse", typeof(T).Name);
            webResponse.Code = httpResponse.StatusCode;
            webResponse.IsValid = this.faultHandler.Handle(httpResponse, validCode);
            if (!webResponse.IsValid)
            {
                webResponse.Fault = this.GetFault(httpResponse);
            }
        }

        private string Location(string uri, HttpResponseMessage httpResponse)
        {
            var srcUri = new Uri(uri);
            return srcUri.GetLeftPart(UriPartial.Authority) + "/" + httpResponse.Headers.Location;
        }

        private Fault GetFault(HttpResponseMessage response)
        {
            Fault fault;
            try
            {
                fault = response.Content.ReadAsAsync<Fault>().Result;
            }
            catch (Exception)
            {
                fault = new Fault { Message = response.StatusCode.ToString() };
            }

            return fault;
        }

        private string GetUserName()
        {
            return ContextInfoProvider.GetUserName();
        }

        private static void CheckAndPopulateRequestInfo(ref MdmRequestInfo requestInfo)
        {
            if (requestInfo == null)
            {
                requestInfo = new MdmRequestInfo();
            }

            if (string.IsNullOrWhiteSpace(requestInfo.RequestId))
            {
                requestInfo.RequestId = Guid.NewGuid().ToString();
            }

            if (string.IsNullOrWhiteSpace(requestInfo.SourceSystem))
            {
                requestInfo.SourceSystem = ConfigurationManager.AppSettings[MdmConstants.MdmRequestSourceSystemName];
            }
        }


        public WebResponse<TMessage> Create<TMessage>(string uri, TMessage message, MdmRequestInfo requestInfo)
        {
            Logger.DebugFormat("Start: MessageRequester.Create {0}", uri);

            CheckAndPopulateRequestInfo(ref requestInfo);

            var requestId = requestInfo.RequestId;

            var webResponse = new WebResponse<TMessage>() { RequestId = requestId };

            this.ResponseHandler(webResponse, uri, client =>
            {
                client.AddHeader(CoreConstants.UserNameHeader, GetUserName());

                client.AddHeader(MdmConstants.MdmRequestHeaderName, requestInfo.Encode());

                var content = new ObjectContent<TMessage>(message, new XmlMediaTypeFormatter());
                using (var response = client.Post(uri, content))
                {
                    this.PopulateWebResponse(webResponse, response, HttpStatusCode.Created);

                    if (webResponse.IsValid)
                    {
                        webResponse.Location = this.Location(uri, response);
                    }
                }
            });

            Logger.DebugFormat("Finish: MessageRequester.Create {0}", uri);

            return webResponse;
        }

        public WebResponse<TMessage> Delete<TMessage>(string uri, MdmRequestInfo requestInfo)
        {
            Logger.DebugFormat("Start: MessageRequester.Delete {0}", uri);

            CheckAndPopulateRequestInfo(ref requestInfo);

            var requestId = requestInfo.RequestId;

            var webResponse = new WebResponse<TMessage>() { RequestId = requestId };

            this.ResponseHandler(webResponse, uri, client =>
            {
                client.AddHeader(CoreConstants.UserNameHeader, GetUserName());
                client.AddHeader(MdmConstants.MdmRequestHeaderName, requestInfo.Encode());

                using (var response = client.Delete(uri))
                {
                    this.PopulateWebResponse(webResponse, response, HttpStatusCode.OK);
                }
            });

            Logger.DebugFormat("Finish: MessageRequester.Delete {0}", uri);

            return webResponse;
        }

        public WebResponse<TMessage> Update<TMessage>(string uri, string etag, TMessage message, MdmRequestInfo requestInfo)
        {
            Logger.DebugFormat("Start: MessageRequester.Update {0}", uri);

            CheckAndPopulateRequestInfo(ref requestInfo);

            var requestId = requestInfo.RequestId;
            var webResponse = new WebResponse<TMessage>() { RequestId = requestId };

            this.ResponseHandler(webResponse, uri, client =>
            {
                client.AddHeader("If-Match", etag);
                client.AddHeader(CoreConstants.UserNameHeader, GetUserName());
                client.AddHeader(MdmConstants.MdmRequestHeaderName, requestInfo.Encode());

                var content = new ObjectContent<TMessage>(message, new XmlMediaTypeFormatter());
                using (var response = client.Post(uri, content))
                {
                    this.PopulateWebResponse(webResponse, response, HttpStatusCode.NoContent);

                    if (webResponse.IsValid)
                    {
                        webResponse.Location = this.Location(uri, response);
                    }
                }
            });

            Logger.DebugFormat("Finish: MessageRequester.Update {0}", uri);

            return webResponse;
        }
    }
}