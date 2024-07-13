﻿using System.IO.Compression;
using System.Text;
using Opc.Ua;
using Opc.Ua.Server;

namespace UaRestGateway.Server.Controllers
{
    public static class MessageUtils
    {
        public static async Task<byte[]> Compress(MemoryStream istrm)
        {
            using (var ostrm = new MemoryStream())
            {
                using (var zstrm = new GZipStream(ostrm, CompressionMode.Compress))
                {
                    await istrm.CopyToAsync(zstrm);
                    zstrm.Close();
                    return ostrm.ToArray();
                }
            }
        }

        public static async Task<MemoryStream> Decompress(Stream istrm)
        {
            using (var zstrm = new GZipStream(istrm, CompressionMode.Decompress))
            {
                var ostrm = new MemoryStream();
                await zstrm.CopyToAsync(ostrm);
                ostrm.Position = 0;
                return ostrm;
            }
        }

        public static string CreateDefaultSession(
            SessionContext context,
            StandardServer server,
            string accessToken)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                var request = new CreateSessionRequest()
                {
                    RequestHeader = new RequestHeader()
                    {
                        Timestamp = DateTime.UtcNow,
                        RequestHandle = 0,
                        ReturnDiagnostics = 0,
                        TimeoutHint = 60000
                    },
                    ClientDescription = new ApplicationDescription()
                    {
                        ApplicationUri = $"urn:{System.Net.Dns.GetHostName().ToLower()}:UA:UaRestGateway:Client",
                        ProductUri = "UaRestGateway",
                        ApplicationName = new LocalizedText("UaRestGateway"),
                        ApplicationType = ApplicationType.Client
                    },
                    ServerUri = server.CurrentInstance.NamespaceUris.GetString(1),
                    EndpointUrl = "opc.tcp://localhost:46040",
                    SessionName = "UaRestGateway:InternalClient",
                    RequestedSessionTimeout = 30000,
                    MaxResponseMessageSize = 0
                };

                var responseHeader = server.CreateSession(
                    request.RequestHeader,
                    request.ClientDescription,
                    request.ServerUri,
                    request.EndpointUrl,
                    request.SessionName,
                    request.ClientNonce,
                    request.ClientCertificate,
                    request.RequestedSessionTimeout,
                    request.MaxResponseMessageSize,
                    out NodeId sessionId,
                    out NodeId authenticationToken,
                    out double revisedSessionTimeout,
                    out byte[] serverNonce,
                    out byte[] serverCertificate,
                    out EndpointDescriptionCollection serverEndpoints,
                    out SignedSoftwareCertificateCollection serverSoftwareCertificates,
                    out SignatureData serverSignature,
                    out uint maxRequestMessageSize);

                var userIdentity = (!String.IsNullOrEmpty(accessToken))
                    ? new ExtensionObject(new IssuedIdentityToken()
                    {
                        PolicyId = SecureChannelContext.Current.EndpointDescription.UserIdentityTokens.Where(x => x.TokenType == UserTokenType.IssuedToken).FirstOrDefault()?.PolicyId,
                        DecryptedTokenData = new UTF8Encoding(false).GetBytes(accessToken)
                    })
                    : null;

                var request2 = new ActivateSessionRequest()
                {
                    RequestHeader = new RequestHeader()
                    {
                        Timestamp = DateTime.UtcNow,
                        RequestHandle = 0,
                        ReturnDiagnostics = 0,
                        TimeoutHint = 60000,
                        AuthenticationToken = authenticationToken
                    },
                    ClientSignature = new SignatureData(),
                    ClientSoftwareCertificates = new SignedSoftwareCertificateCollection(),
                    LocaleIds = new StringCollection(),
                    UserTokenSignature = new SignatureData()
                };

                responseHeader = server.ActivateSession(
                    request2.RequestHeader,
                    request2.ClientSignature,
                    request2.ClientSoftwareCertificates,
                    request2.LocaleIds,
                    userIdentity,
                    request2.UserTokenSignature,
                    out serverNonce,
                    out StatusCodeCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new ActivateSessionResponse()
                {
                    ResponseHeader = responseHeader,
                    ServerNonce = serverNonce,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return authenticationToken.Format(server.CurrentInstance.MessageContext, true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static async Task<IServiceRequest> Decode<T>(IServiceMessageContext context, Stream stream, bool? compressed = null) where T : IServiceRequest
        {
            var mstrm = stream;

            if (compressed == null)
            {
                mstrm = new MemoryStream();
                await stream.CopyToAsync(mstrm);
                mstrm.Position = 0;

                if (mstrm.ReadByte() == 0x1F && mstrm.ReadByte() == 0x8B)
                {
                    mstrm = await Decompress(mstrm);
                }
                else
                {
                    mstrm.Position = 0;
                }
            }
            else if (compressed == true)
            {
                mstrm = await Decompress(stream);
            }

            using (var reader = new StreamReader(mstrm))
            {
                var json = await reader.ReadToEndAsync();

                using (var decoder = new JsonDecoder(json, context))
                {
                    decoder.UpdateNamespaceTable = true;
                    SessionLessServiceMessage message = new SessionLessServiceMessage();

                    // message.NamespaceUris = new NamespaceTable(decoder.ReadStringArray(nameof(SessionLessServiceMessage.NamespaceUris)));
                    // message.ServerUris = new StringTable(decoder.ReadStringArray(nameof(SessionLessServiceMessage.ServerUris)));
                    // decoder.SetMappingTables(message.NamespaceUris, message.ServerUris);

                    message.LocaleIds = new StringTable(decoder.ReadStringArray(nameof(SessionLessServiceMessage.LocaleIds)));

                    var serviceId = decoder.ReadUInt32("ServiceId");
                    var serviceType = decoder.Context.Factory.GetSystemType(new NodeId(serviceId, 0));

                    if (serviceType == null)
                    {
                        serviceType = typeof(T);
                    }

                    message.Message = decoder.ReadEncodeable("Body", serviceType);
                    var request = message.Message as IServiceRequest;
                    return request;
                }
            }
        }

        public static async Task<MemoryStream> Encode<T>(IServiceMessageContext context, T response, bool compress = false) where T : IEncodeable
        {
            var stream = new MemoryStream();

            using (var encoder = new JsonEncoder(context, true, stream: stream, leaveOpen: true))
            {
                encoder.UseStringNodeIds = true;
                encoder.ForceNamespaceUri = true;
                encoder.ForceNamespaceUriForIndex1 = true;

                encoder.WriteStringArray(nameof(SessionLessServiceMessage.LocaleIds), null);
                encoder.WriteUInt32(nameof(SessionLessServiceMessage.ServiceId), (uint)response.TypeId.Identifier);
                encoder.WriteEncodeable("Body", response, typeof(T));

                encoder.Close();
                stream.Position = 0;
            }

            if (compress)
            {
                return new MemoryStream(await Compress(stream));
            }

            return stream;
        }

        public static IServiceResponse Fault(IServiceRequest request, Exception e)
        {
            var sr = new ServiceResult(e, Opc.Ua.StatusCodes.BadUnexpectedError);

            ServiceFault fault = new ServiceFault()
            {
                ResponseHeader = new ResponseHeader()
                {
                    RequestHandle = request?.RequestHeader?.RequestHandle ?? 0,
                    Timestamp = DateTime.UtcNow,
                    ServiceResult = sr.StatusCode
                }
            };

            if (((request?.RequestHeader?.ReturnDiagnostics ?? 0) & (uint)DiagnosticsMasks.ServiceLocalizedText) != 0)
            {
                fault.ResponseHeader.StringTable = new StringCollection(new string[] { sr.ToString() });
                fault.ResponseHeader.ServiceDiagnostics = new DiagnosticInfo() { LocalizedText = 0 };
            }

            return fault;
        }

        public static Task<IServiceResponse> CloseSession(
            SessionContext context,
            StandardServer server,
            CloseSessionRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.CloseSession(
                    request.RequestHeader,
                    request.DeleteSubscriptions);

                var response = new CloseSessionResponse()
                {
                    ResponseHeader = responseHeader
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> Read(
            SessionContext context,
            StandardServer server,
            ReadRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.Read(
                    request.RequestHeader,
                    request.MaxAge,
                    request.TimestampsToReturn,
                    request.NodesToRead,
                    out DataValueCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new ReadResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> Write(
            SessionContext context,
            StandardServer server,
            WriteRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.Write(
                    request.RequestHeader,
                    request.NodesToWrite,
                    out StatusCodeCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new WriteResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> Call(
            SessionContext context,
            StandardServer server,
            CallRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.Call(
                    request.RequestHeader,
                    request.MethodsToCall,
                    out CallMethodResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new CallResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> Browse(
            SessionContext context,
            StandardServer server,
            BrowseRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.Browse(
                    request.RequestHeader,
                    request.View,
                    request.RequestedMaxReferencesPerNode,
                    request.NodesToBrowse,
                    out BrowseResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new BrowseResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> BrowseNext(
            SessionContext context,
            StandardServer server,
            BrowseNextRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.BrowseNext(
                    request.RequestHeader,
                    request.ReleaseContinuationPoints,
                    request.ContinuationPoints,
                    out BrowseResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new BrowseNextResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> TranslateBrowsePathsToNodeIds(
            SessionContext context,
            StandardServer server,
            TranslateBrowsePathsToNodeIdsRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.TranslateBrowsePathsToNodeIds(
                    request.RequestHeader,
                    request.BrowsePaths,
                    out BrowsePathResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new TranslateBrowsePathsToNodeIdsResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> HistoryRead(
            SessionContext context,
            StandardServer server,
            HistoryReadRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.HistoryRead(
                    request.RequestHeader,
                    request.HistoryReadDetails,
                    request.TimestampsToReturn,
                    request.ReleaseContinuationPoints,
                    request.NodesToRead,
                    out HistoryReadResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new HistoryReadResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> HistoryUpdate(
            SessionContext context,
            StandardServer server,
            HistoryUpdateRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    request.RequestHeader.AuthenticationToken = context.AuthenticationToken;
                }

                var responseHeader = server.HistoryUpdate(
                    request.RequestHeader,
                    request.HistoryUpdateDetails,
                    out HistoryUpdateResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new HistoryUpdateResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> CreateSession(
            SessionContext context,
            StandardServer server,
            CreateSessionRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                var responseHeader = server.CreateSession(
                    request.RequestHeader,
                    request.ClientDescription,
                    request.ServerUri,
                    request.EndpointUrl,
                    request.SessionName,
                    request.ClientNonce,
                    request.ClientCertificate,
                    request.RequestedSessionTimeout,
                    request.MaxResponseMessageSize,
                    out NodeId sessionId,
                    out NodeId authenticationToken,
                    out double revisedSessionTimeout,
                    out byte[] serverNonce,
                    out byte[] serverCertificate,
                    out EndpointDescriptionCollection serverEndpoints,
                    out SignedSoftwareCertificateCollection serverSoftwareCertificates,
                    out SignatureData serverSignature,
                    out uint maxRequestMessageSize);

                serverEndpoints = new EndpointDescriptionCollection(new EndpointDescription[] { SecureChannelContext.Current.EndpointDescription });

                var response = new CreateSessionResponse()
                {
                    ResponseHeader = responseHeader,
                    SessionId = sessionId,
                    AuthenticationToken = authenticationToken,
                    RevisedSessionTimeout = revisedSessionTimeout,
                    ServerNonce = serverNonce,
                    ServerCertificate = serverCertificate,
                    ServerEndpoints = serverEndpoints,
                    ServerSoftwareCertificates = serverSoftwareCertificates,
                    ServerSignature = serverSignature,
                    MaxRequestMessageSize = maxRequestMessageSize
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> ActivateSession(
            SessionContext context,
            StandardServer server,
            ActivateSessionRequest request,
            string accessToken = null)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                var userIdentity =
                    (request?.UserIdentityToken?.Body == null)
                    ? request.UserIdentityToken
                    : ((!String.IsNullOrEmpty(accessToken))
                    ? new ExtensionObject(new IssuedIdentityToken()
                    {
                        PolicyId = SecureChannelContext.Current.EndpointDescription.UserIdentityTokens.Where(x => x.TokenType == UserTokenType.IssuedToken).FirstOrDefault()?.PolicyId,
                        DecryptedTokenData = new UTF8Encoding(false).GetBytes(accessToken)
                    })
                    : null);

                var responseHeader = server.ActivateSession(
                    request.RequestHeader,
                    request.ClientSignature,
                    request.ClientSoftwareCertificates,
                    request.LocaleIds,
                    userIdentity,
                    request.UserTokenSignature,
                    out byte[] serverNonce,
                    out StatusCodeCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new ActivateSessionResponse()
                {
                    ResponseHeader = responseHeader,
                    ServerNonce = serverNonce,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> CreateSubscription(
            SessionContext context,
            StandardServer server,
            CreateSubscriptionRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    throw new ServiceResultException(Opc.Ua.StatusCodes.BadSessionIdInvalid, "Session not specified.");
                }

                var responseHeader = server.CreateSubscription(
                    request.RequestHeader,
                    request.RequestedPublishingInterval,
                    request.RequestedLifetimeCount,
                    request.RequestedMaxKeepAliveCount,
                    request.MaxNotificationsPerPublish,
                    request.PublishingEnabled,
                    request.Priority,
                    out uint subscriptionId,
                    out double revisedPublishingInterval,
                    out uint revisedLifetimeCount,
                    out uint revisedMaxKeepAliveCount);

                var response = new CreateSubscriptionResponse()
                {
                    ResponseHeader = responseHeader,
                    SubscriptionId = subscriptionId,
                    RevisedPublishingInterval = revisedPublishingInterval,
                    RevisedLifetimeCount = revisedLifetimeCount,
                    RevisedMaxKeepAliveCount = revisedMaxKeepAliveCount
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> DeleteSubscriptions(
            SessionContext context,
            StandardServer server,
            DeleteSubscriptionsRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    throw new ServiceResultException(Opc.Ua.StatusCodes.BadSessionIdInvalid, "Session not specified.");
                }

                var responseHeader = server.DeleteSubscriptions(
                    request.RequestHeader,
                    request.SubscriptionIds,
                    out StatusCodeCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                var response = new DeleteSubscriptionsResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> Publish(
            SessionContext context,
            StandardServer server,
            PublishRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    throw new ServiceResultException(Opc.Ua.StatusCodes.BadSessionIdInvalid, "Session not specified.");
                }

                var responseHeader = server.Publish(
                    request.RequestHeader,
                    request.SubscriptionAcknowledgements,
                    out var subscriptionId,
                    out var availableSequenceNumbers,
                    out var moreNotifications,
                    out var notificationMessage,
                    out var results,
                    out var diagnosticInfos);

                var response = new PublishResponse()
                {
                    ResponseHeader = responseHeader,
                    SubscriptionId = subscriptionId,
                    AvailableSequenceNumbers = availableSequenceNumbers,
                    MoreNotifications = moreNotifications,
                    NotificationMessage = notificationMessage,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> CreateMonitoredItems(
            SessionContext context,
            StandardServer server,
            CreateMonitoredItemsRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    throw new ServiceResultException(Opc.Ua.StatusCodes.BadSessionIdInvalid, "Session not specified.");
                }

                var responseHeader = server.CreateMonitoredItems(
                    request.RequestHeader,
                    request.SubscriptionId,
                    request.TimestampsToReturn,
                    request.ItemsToCreate,
                    out var results,
                    out var diagnosticInfos);

                var response = new CreateMonitoredItemsResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> ModifyMonitoredItems(
            SessionContext context,
            StandardServer server,
            ModifyMonitoredItemsRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    throw new ServiceResultException(Opc.Ua.StatusCodes.BadSessionIdInvalid, "Session not specified.");
                }

                var responseHeader = server.ModifyMonitoredItems(
                    request.RequestHeader,
                    request.SubscriptionId,
                    request.TimestampsToReturn,
                    request.ItemsToModify,
                    out var results,
                    out var diagnosticInfos);

                var response = new ModifyMonitoredItemsResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }

        public static Task<IServiceResponse> DeleteMonitoredItems(
            SessionContext context,
            StandardServer server,
            DeleteMonitoredItemsRequest request)
        {
            try
            {
                SecureChannelContext.Current = context.ChannelContext;

                if (NodeId.IsNull(request.RequestHeader.AuthenticationToken))
                {
                    throw new ServiceResultException(Opc.Ua.StatusCodes.BadSessionIdInvalid, "Session not specified.");
                }

                var responseHeader = server.DeleteMonitoredItems(
                    request.RequestHeader,
                    request.SubscriptionId,
                    request.MonitoredItemIds,
                    out var results,
                    out var diagnosticInfos);

                var response = new DeleteMonitoredItemsResponse()
                {
                    ResponseHeader = responseHeader,
                    Results = results,
                    DiagnosticInfos = diagnosticInfos
                };

                return Task.FromResult<IServiceResponse>(response);
            }
            catch (Exception e)
            {
                return Task.FromResult(Fault(request, e));
            }
        }
    }
}

public class SessionContext
{
    public bool IsActivated { get; set; }

    public NodeId AuthenticationToken { get; set; }

    public SecureChannelContext ChannelContext { get; set; }
}