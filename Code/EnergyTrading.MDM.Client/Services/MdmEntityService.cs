﻿namespace EnergyTrading.Mdm.Client.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Web;
    using EnergyTrading.Contracts.Search;
    using EnergyTrading.Logging;
    using EnergyTrading.Mdm.Client.Extensions;
    using EnergyTrading.Mdm.Client.WebClient;
    using EnergyTrading.Mdm.Contracts;

    public class MdmEntityService<TContract> : IMdmEntityService<TContract>
        where TContract : class, IMdmEntity
    {
        private readonly static ILogger Logger = LoggerFactory.GetLogger(typeof(MdmEntityService<TContract>));
        private readonly Dictionary<int, string> etags;
        private readonly IMessageRequester requester;
        private readonly string entityUri;
        private readonly string entityListUri;
        private readonly string mapUri;
        private readonly string crossMapUri;
        private readonly string mappingUri;
        private readonly string deleteMappingUri;
        private readonly string searchUri;
        private readonly string validAtParam;
        private readonly string entityName;

        private const string DateFormatString = "yyyy-MM-dd'T'HH:mm:ss.fffffffZ";

        public MdmEntityService(string baseUri, IMessageRequester requester)
        {
            this.BaseUri = baseUri;
            this.entityUri = baseUri + "/{0}";
            this.mappingUri = this.entityUri + "/mapping";
            this.entityListUri = this.entityUri + "/list";
            this.deleteMappingUri = this.mappingUri + "/{1}";
            this.mapUri = baseUri + "/map?" + QueryConstants.SourceSystem + "={0}&" + QueryConstants.MappingValue + "={1}";
            this.crossMapUri = baseUri + "/crossmap?" + QueryConstants.SourceSystem + "={0}&" + QueryConstants.MappingValue + "={1}&" + QueryConstants.DestinationSystem + "={2}";
            this.searchUri = baseUri + "/search";
            this.validAtParam = "?" + QueryConstants.ValidAt + "={0}";
            this.entityName = typeof (TContract).Name;

            this.requester = requester;
            this.etags = new Dictionary<int, string>();
        }

        public int Count
        {
            get { return 0; }
        }

        public int MappingCount
        {
            get { return 0; }
        }

        protected string BaseUri { get; set; }

        public void Clear()
        {
            this.etags.Clear();
        }

        public WebResponse<MdmId> CreateMapping(int id, MdmId identifier)
        {
            return CreateMapping(id, identifier, null);
        }

        public WebResponse<TContract> DeleteMapping(int entityId, int mappingId)
        {
            return DeleteMapping(entityId, mappingId, null);
        }

        public WebResponse<TContract> Get(int id)
        {
            // NB We push null here so that the service inteprets now and any requests can be cached upstream
            return this.Get(id, null);
        }

        public WebResponse<IList<TContract>> GetList(int id)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.GetList<{0}>: {1} - {2}", typeof(TContract).Name, id);
            }

            var response = this.requester.Request<IList<TContract>>(string.Format(this.entityListUri, id));

            response.LogResponse();

            return response;
        }

        public WebResponse<TContract> Get(int id, DateTime? validAt)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.Get<{0}>: {1} - {2}", typeof(TContract).Name, id, validAt);
            }
            return this.AcquireEntity(id, validAt);
        }

        public WebResponse<TContract> Get(MdmId identifier)
        {
            // NB We push null here so that the service inteprets now and any requests can be cached upstream
            return this.Get(identifier, null);
        }

        public WebResponse<TContract> Get(MdmId identifier, DateTime? validAt)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.Get<{0}>: {1} {2}", typeof(TContract).Name, identifier, validAt);
            }
            if (identifier == null)
            {
                throw new ArgumentNullException("identifier");
            }

            if (identifier.IsMdmId || identifier.SystemName == SourceSystemNames.Nexus)
            {
                int id;
                if (int.TryParse(identifier.Identifier, out id))
                {
                    return this.Get(id, validAt);
                }

                throw new ArgumentException("Invalid Nexus identifier: {0}.", identifier.ToString());
            }

            return this.AcquireEntity(identifier, validAt);
        }

        public WebResponse<TContract> Create(TContract contract)
        {
            return Create(contract, null);
        }

        public WebResponse<MdmId> GetMapping(int id, Predicate<MdmId> query)
        {
            Logger.DebugFormat("Start : MdmEntityService.GetMapping<{0}> - {1}", this.entityName, id);
            var response = this.Get(id);
            if (response.IsValid)
            {
                Logger.Debug("MdmEntityService.GetMapping : Valid response received. Fetching nexusId based on query");
                var nexusId = response.Message.Identifiers.FirstOrDefault(mapping => query(mapping));
                if (nexusId != null)
                {
                    var r2 = new WebResponse<MdmId>
                    {
                        Code = HttpStatusCode.OK,
                        Message = nexusId
                    };

                    r2.LogResponse();
                    return r2;
                }

                // TODO: Add fault for "No mapping to system x"
                response.Code = HttpStatusCode.NotFound;
            }

            var webResponse = new WebResponse<MdmId>
            {
                Code = response.Code,
                IsValid = false,
                Fault = response.Fault
            };

            webResponse.LogResponse();

            Logger.DebugFormat("Stop : MdmEntityService.GetMapping<{0}> - {1}", this.entityName, id);

            return webResponse;
        }

        public void Invalidate(int id)
        {
            string value;
            if (!this.etags.TryGetValue(id, out value))
            {
                return;
            }

            this.etags.Remove(id);
        }

        public WebResponse<MappingResponse> CrossMap(MdmId identifier, string targetSystem)
        {
            if (identifier == null)
            {
                throw new ArgumentNullException("identifier");
            }

            return this.CrossMap(identifier.SystemName, identifier.Identifier, targetSystem);
        }

        public WebResponse<MappingResponse> CrossMap(string sourceSystem, string identifier, string targetSystem)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.CrossMap<{0}>: {1} {2} {3}", typeof(TContract).Name, sourceSystem, identifier, targetSystem);
            }

            var response = this.requester.Request<MappingResponse>(string.Format(this.crossMapUri, sourceSystem, identifier, targetSystem));

            response.LogResponse();

            return response;
        }

        public WebResponse<MdmId> Map(int id, string targetSystem)
        {
            return this.GetMapping(id, ident => string.Equals(ident.SystemName, targetSystem, StringComparison.InvariantCultureIgnoreCase));
        }

        public PagedWebResponse<IList<TContract>> Search(Search search)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.Search<{0}>", typeof(TContract).Name);
            }

            var response = this.requester.Search<TContract>(this.searchUri, search);

            response.LogResponse();

            return response;
        }

        public WebResponse<TContract> Update(int id, TContract contract)
        {
            return this.Update(id, contract, this.etags[id]);
        }

        public WebResponse<TContract> Update(int id, TContract contract, string etag)
        {
            return Update(id, contract, etag, null);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="contract"></param>
        /// <param name="requestInfo"></param>
        /// <returns></returns>
        public WebResponse<TContract> Create(TContract contract, MdmRequestInfo requestInfo)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.Create<{0}>", typeof(TContract).Name);
            }

            var response = this.Create(this.BaseUri, contract, requestInfo);

            if (response.IsValid)
            {
                this.ProcessContract(response);
            }

            response.LogResponse();

            return response;
        }

        public WebResponse<MdmId> CreateMapping(int id, MdmId identifier, MdmRequestInfo requestInfo)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.CreateMapping<{0}>: {1} - {2}", this.entityName, id, identifier);
            }
            var mapping = new Mapping
            {
                // TODO: Flesh out the rest
                SystemName = identifier.SystemName,
                Identifier = identifier.Identifier,
                DefaultReverseInd = identifier.DefaultReverseInd,
            };

            var uri = string.Format(this.mappingUri, id);
            var response = this.Create<MdmId, MappingResponse>(uri, mapping, requestInfo);
            WebResponse<MdmId> webResponse;

            if (response.IsValid)
            {
                webResponse = new WebResponse<MdmId>
                {
                    Code = HttpStatusCode.OK,
                    Message = response.Message.Mappings[0],
                    RequestId = response.RequestId
                };
            }
            else
            {
                webResponse = new WebResponse<MdmId>
                {
                    IsValid = false,
                    Code = response.Code,
                    Fault = response.Fault,
                    RequestId = response.RequestId
                };
            }

            webResponse.LogResponse();

            return webResponse;
        }

        public WebResponse<TContract> DeleteMapping(int entityId, int mappingId, MdmRequestInfo requestInfo)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.DeleteMapping<{0}>: {1} - {2}", this.entityName, entityId, mappingId);
            }

            var uri = string.Format(this.deleteMappingUri, entityId, mappingId);

            Logger.DebugFormat("MdmEntityService.DeleteMapping : Uri - {0}", uri);

            var response = this.requester.Delete<TContract>(uri, requestInfo);

            if (response.IsValid)
            {
                response = new WebResponse<TContract>
                {
                    Code = HttpStatusCode.OK,
                    IsValid = true,
                    RequestId = response.RequestId
                };
            }
            else
            {
                response = new WebResponse<TContract>
                {
                    Code = response.Code,
                    IsValid = false,
                    Fault = response.Fault,
                    RequestId = response.RequestId
                };
            }

            response.LogResponse();

            return response;
        }

        public WebResponse<TContract> Update(int id, TContract contract, MdmRequestInfo requestInfo)
        {
            return Update(id, contract, this.etags[id], requestInfo);
        }

        public WebResponse<TContract> Update(int id, TContract contract, string etag, MdmRequestInfo requestInfo)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.DebugFormat("MdmEntityService.Update<{0}>: {1} {2}", this.entityName, id, etag);
            }

            var uri = string.Format(this.entityUri, id);

            Logger.DebugFormat("MdmEntityService.Update : Uri - {0}", uri);

            var response = this.requester.Update(uri, etag, contract, requestInfo);

            response.LogResponse();

            if (!response.IsValid)
            {
                return response;
            }

            var queryString = string.Empty;
            if (contract.MdmSystemData != null && contract.MdmSystemData.StartDate.HasValue)
            {
                queryString = string.Format(this.validAtParam, contract.MdmSystemData.StartDate.Value.ToString(DateFormatString));
            }

            string location = response.Location + queryString;

            Logger.DebugFormat("MdmEntityService.Update : Received valid response. Now requesting - {0}", location);
            
            var r2 = this.requester.Request<TContract>(location);

            r2.RequestId = response.RequestId;

            // TODO: Should this just be a IsValid check?
            if (r2.Code == HttpStatusCode.OK)
            {
                this.ProcessContract(r2);
            }

            r2.LogResponse();

            return r2;
        }

        private WebResponse<TContract> AcquireEntity(int id, DateTime? validAt)
        {
            try
            {
                Logger.DebugFormat("Start : MdmEntityService.AcquireEntity<{0}> - {1}", this.entityName, id);
                var uri = string.Format(this.entityUri, id);
                if (validAt.HasValue)
                {
                    uri += string.Format(this.validAtParam, validAt.Value.ToString(DateFormatString));
                }


                var response = this.GetContract(uri);

                response.LogResponse();

                return response;
            }
            finally
            {
                Logger.DebugFormat("Stop : MdmEntityService.AcquireEntity<{0}> - {1}", this.entityName, id);
            }
        }

        private WebResponse<TContract> AcquireEntity(MdmId sourceIdentifier, DateTime? validAt)
        {
            try
            {
                Logger.DebugFormat("Start : MdmEntityService.AcquireEntity<{0}> - {1}", this.entityName, sourceIdentifier);
                var uri = string.Format(this.mapUri, UrlEncode(sourceIdentifier.SystemName), UrlEncode(sourceIdentifier.Identifier));
                if (validAt.HasValue)
                {
                    uri += string.Format(this.validAtParam, validAt.Value.ToString(DateFormatString));
                }

                var response = this.GetContract(uri);

                response.LogResponse();

                return response;
            }
            finally
            {
                Logger.DebugFormat("Stop : MdmEntityService.AcquireEntity<{0}> - {1}", this.entityName, sourceIdentifier);
            }
        }

        private WebResponse<TMessage> Create<TMessage>(string uri, TMessage message, MdmRequestInfo requestInfo) where TMessage : class
        {
            return this.Create<TMessage, TMessage>(uri, message, requestInfo);
        }

        private WebResponse<TResponse> Create<TMessage, TResponse>(string uri, TMessage message, MdmRequestInfo requestInfo) where TResponse : class
        {
            try
            {
                Logger.DebugFormat("Start : MdmEntityService.Create : Uri - {0}", uri);

                var request = this.requester.Create(uri, message, requestInfo);
                var response = new WebResponse<TResponse> { Code = request.Code, IsValid = false, RequestId = request.RequestId, Fault = request.Fault };

                if (request.IsValid)
                {
                    Logger.DebugFormat("MdmEntityService.Create : Valid Response received. Now requesting - {0}", request.Location);
                    response = this.requester.Request<TResponse>(request.Location);
                    response.RequestId = request.RequestId;
                }

                response.LogResponse();

                return response;
            }
            finally
            {
                Logger.DebugFormat("Stop : MdmEntityService.Create");
            }
        }

        private WebResponse<TContract> GetContract(string uri)
        {
            Logger.DebugFormat("MdmEntityService.GetContract : Uri - {0}", uri);

            var entity = this.Request<TContract>(uri);
            if (entity.IsValid)
            {
                // Only process if we found data!
                this.ProcessContract(entity);
            }

            return entity;
        }

        private void ProcessContract(WebResponse<TContract> reponse)
        {
            var entity = reponse.Message;
            var id = entity.ToMdmKey();

            this.etags[id] = reponse.Tag;
        }

        private WebResponse<TMessage> Request<TMessage>(string uri)
            where TMessage : class
        {
            return this.requester.Request<TMessage>(uri);
        }

        private static string UrlEncode(string value)
        {
            return HttpUtility.UrlEncode(value);
        }
    }
}