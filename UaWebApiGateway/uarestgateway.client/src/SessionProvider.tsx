import React from 'react';
import useWebSocket, { ReadyState } from 'react-use-websocket';
import { UserContext } from './UserProvider';
import * as OpcUa from 'opcua-webapi';
import { UserLoginStatus } from './user';
import { IRequestMessage } from './service/IRequestMessage';
import { IResponseMessage } from './service/IResponseMessage';
import { ICompletedRequest } from './service/ICompletedRequest';
import { SessionState } from './service/SessionState';
import { HandleFactory } from './service/HandleFactory';
import { call } from './opcua-utils';

export const DefaultServerUrl = `wss://${location.host}/stream`;

import { SessionContext } from './SessionContext';

export interface ISessionContext {
   serverUrl: string,
   setServerUrl: (value: string) => void,
   isConnected: boolean,
   sessionState: SessionState,
   isEnabled: boolean,
   setIsEnabled: (value: boolean) => void,
   isSessionEnabled: boolean,
   setIsSessionEnabled: (value: boolean) => void,
   requestTimeout: number,
   setRequestTimeout: (value: number) => void,
   sendRequest: (request: IRequestMessage, callerHandle?: number) => void
   messageCounter?: number,
   processMessages: (matcher: (message: ICompletedRequest) => boolean) => ICompletedRequest[]
}
interface SessionProps {
   children?: React.ReactNode
}

interface SessionInternals {
   serverUrl: string | null,
   isConnected: boolean,
   sessionState: SessionState,
   isEnabled: boolean,
   isSessionEnabled: boolean,
   requestTimeout: number,
   authenticationToken?: string,
   serverNonce?: string,
   requests: Map<number, ICompletedRequest>,
   responses: ICompletedRequest[]
}

const apiNames = {
   [OpcUa.DataTypeIds.BrowseRequest]: { path: "browse", response: OpcUa.DataTypeIds.BrowseResponse },
   [OpcUa.DataTypeIds.BrowseNextRequest]: { path: "browsenext", response: OpcUa.DataTypeIds.BrowseNextResponse }, 
   [OpcUa.DataTypeIds.ReadRequest]: { path: "read", response: OpcUa.DataTypeIds.ReadResponse },
   [OpcUa.DataTypeIds.WriteRequest]: { path: "write", response: OpcUa.DataTypeIds.WriteResponse },
   [OpcUa.DataTypeIds.CallRequest]: { path: "call", response: OpcUa.DataTypeIds.CallResponse },
   [OpcUa.DataTypeIds.TranslateBrowsePathsToNodeIdsRequest]: { path: "translate", response: OpcUa.DataTypeIds.TranslateBrowsePathsToNodeIdsResponse },
   [OpcUa.DataTypeIds.HistoryReadRequest]: { path: "historyread", response: OpcUa.DataTypeIds.HistoryReadResponse }, 
   [OpcUa.DataTypeIds.HistoryUpdateRequest]: { path: "historyupdate", response: OpcUa.DataTypeIds.HistoryUpdateResponse }
};

function getApiName(responseId: string): string | undefined {
   for (const [, value] of Object.entries(apiNames)) {
      if (value.response === responseId) {
         return value.path;
      }
   }
   return undefined; 
}

export const SessionProvider = ({ children }: SessionProps) => {
   const [serverUrl, setServerUrl] = React.useState<string | null>(DefaultServerUrl);
   const [isEnabled, setIsEnabled] = React.useState<boolean>(false);
   const [isSessionEnabled, setIsSessionEnabled] = React.useState<boolean>(false);
   const [sessionState, setSessionState] = React.useState<SessionState>(SessionState.Disconnected);
   const [messageCounter, setMessageCounter] = React.useState<number>(0);

   const { user, loginStatus } = React.useContext(UserContext);

   const m = React.useRef<SessionInternals>({
      serverUrl: null,
      isConnected: false,
      sessionState: SessionState.Disconnected,
      isEnabled: false,
      isSessionEnabled: false,
      requestTimeout: 60000,
      requests: new Map<number, ICompletedRequest>(),
      responses: []
   });

   const handleOnMessage = React.useCallback((event: MessageEvent) => {
      try {
         const response = JSON.parse(event.data) as IResponseMessage;
         processRawResponse(response);
      } catch (error) {
         console.warn('SessionProvider:WebSocket:OnMesssage', error);
      }
   }, []);

   const { sendMessage, readyState } = useWebSocket(
      (isEnabled) ? serverUrl : null,
      {
         share: true,
         protocols: (user?.accessToken) ? ["opcua+uajson", `opcua+token+${user?.accessToken}`] : ["opcua+uajson"],
         shouldReconnect: () => isEnabled,
         onMessage: handleOnMessage
      },
   );

   const processRawResponse = React.useCallback((response: IResponseMessage) => {
      if (response) {
         const callerHandle = response.Body?.ResponseHeader?.RequestHandle ?? 0;
         const request = m.current.requests.get(callerHandle);
         if (request) {
            m.current.requests.delete(callerHandle);
            request.response = response;
            m.current.responses.push(request);
            // console.error("Session SUB (" + Array.from(m.current.requests.keys()).join(",") + "): " + callerHandle);
            // console.error(`===>>> RESPONSE: ${getApiName(response?.ServiceId ?? '')} ${callerHandle}`);
            setMessageCounter(x => x + 1);
         }
      }
   }, [])

   const sendRequest = React.useCallback((request: IRequestMessage, callerHandle?: number) => {
      if (!request.Body.RequestHeader) {
         request.Body.RequestHeader = {}
      }
      const requestHeader = request.Body.RequestHeader;
      requestHeader.RequestHandle = HandleFactory.increment();
      if (!requestHeader.Timestamp) {
         requestHeader.Timestamp = new Date();
      }
      if (!requestHeader.TimeoutHint) {
         requestHeader.TimeoutHint = m.current.requestTimeout;
      }
      if (!requestHeader.AuthenticationToken) {
         requestHeader.AuthenticationToken = m.current.authenticationToken;
      }
      m.current.requests.set(
         requestHeader.RequestHandle,
         { callerHandle: callerHandle ?? requestHeader.RequestHandle, request }
      );
      // console.error("Session ADD (" + Array.from(m.current.requests.keys()).join(",") + "): " + (requestHeader.RequestHandle ?? 0));

      try {
         if (readyState === ReadyState.OPEN) {
            sendMessage(JSON.stringify(request));
         }
         else {
            const callerHandle = requestHeader.RequestHandle;
            // console.error(`===>>> REQUEST: ${apiNames[request.ServiceId ?? ''].path} ${requestHeader.RequestHandle}`);

            call(
               `/opcua/${apiNames[request.ServiceId ?? ''].path}`,
               { callerHandle: callerHandle, request: { Body: request.Body } },
               undefined,
               user,
               true)
               .then(response => {
                  if (response.code) {
                     processRawResponse({
                        ServiceId: apiNames[request.ServiceId ?? ''].response,
                        Body: {
                           ResponseHeader: {
                              RequestHandle: callerHandle,
                              ServiceResult: response.code,
                              ServiceDiagnostics: { LocalizedText: 0 }, 
                              StringTable: [response.message]
                           }
                        }
                     })
                  }
                  else {
                     processRawResponse({
                        ServiceId: apiNames[request.ServiceId ?? ''].response,
                        Body: response
                     })
                  }
               })
               .catch(error => {
                  console.error('Unexpected HTTP error:', error);
                  m.current.requests.delete(callerHandle);
               });
         }
      } catch (error) {
         console.error('Failed to send request:', error);
      }
   }, [sendMessage, processRawResponse, readyState, user]);

   const processMessages = React.useCallback((matcher: (message: ICompletedRequest) => boolean): ICompletedRequest[] => {
      const responses = m.current.responses;
      const matched: ICompletedRequest[] = [];
      const remaining: ICompletedRequest[] = [];
      for (const ii of responses) {
         if (matcher(ii)) {
            matched.push(ii);
         } else {
            remaining.push(ii);
         }
      }
      m.current.responses = remaining;
      return matched;
   }, []);

   const activateSession = React.useCallback((createSessionResponse: OpcUa.CreateSessionResponse) => {
      const endpoint = createSessionResponse?.ServerEndpoints?.find((endpoint) => {
         if (endpoint.TransportProfileUri === "http://opcfoundation.org/UA-Profile/Transport/wss-uajson") {
            return endpoint;
         }
         return null;
      });
      let token: OpcUa.ExtensionObject | undefined = undefined;
      if (loginStatus === UserLoginStatus.LoggedIn && user.accessToken) {
         const policy = endpoint?.UserIdentityTokens?.find(
            (token) => (token.IssuedTokenType === 'http://opcfoundation.org/UA/UserToken#JWT') ? token : undefined
         );
         token = {
            "@TypeId": OpcUa.DataTypeIds.IssuedIdentityToken,
            PolicyId: policy?.PolicyId,
            TokenData: btoa(user.accessToken ?? '')
         } as OpcUa.ExtensionObject;
      }
      const request: OpcUa.ActivateSessionRequest = {
         RequestHeader: {
            AuthenticationToken: createSessionResponse?.AuthenticationToken
         },
         LocaleIds: ["en"],
         UserIdentityToken: token
      }
      const message: IRequestMessage = {
         ServiceId: OpcUa.DataTypeIds.ActivateSessionRequest,
         Body: request
      };
      sendRequest(message);
   }, [sendRequest, loginStatus, user?.accessToken]);

   const createSession = React.useCallback(() => {
      const request: OpcUa.CreateSessionRequest = {
         ClientDescription: {
            ApplicationUri: 'urn:localhost:UA:uarestgateway.client',
            ProductUri: 'uarestgateway.client',
            ApplicationName: { Text: 'uarestgateway.client' },
            ApplicationType: OpcUa.ApplicationType.Client
         },
         EndpointUrl: window.location.href.split('?')[0],
         SessionName: 'uarestgateway.client',
         ClientNonce: undefined,
         ClientCertificate: undefined,
         RequestedSessionTimeout: 120000,
         MaxResponseMessageSize: 8 * 1014 * 1024
      };
      const message: IRequestMessage = {
         ServiceId: OpcUa.DataTypeIds.CreateSessionRequest,
         Body: request
      };
      sendRequest(message);
   }, [sendRequest]);

   const deleteSession = React.useCallback(() => {
      const request: OpcUa.CloseSessionRequest = {
         DeleteSubscriptions: true
      };
      const message: IRequestMessage = {
         ServiceId: OpcUa.DataTypeIds.CloseSessionRequest,
         Body: request
      };
      sendRequest(message);
   }, [sendRequest]);

   React.useEffect(() => {
      switch (readyState) {
         case ReadyState.CONNECTING:
            m.current.sessionState = SessionState.Connecting;
            setSessionState(m.current.sessionState);
            break;
         case ReadyState.OPEN:
            m.current.sessionState = SessionState.NoSession;
            if (m.current.isSessionEnabled) {
               createSession();
               m.current.sessionState = SessionState.Creating;
               setSessionState(m.current.sessionState);
            }
            break;
         case ReadyState.CLOSED:
            if (m.current.sessionState === SessionState.SessionActive) {
               deleteSession();
            }
            m.current.sessionState = SessionState.Disconnected;
            setSessionState(m.current.sessionState);
            break;
         default:
            break;
      }
   }, [readyState, createSession, deleteSession])

   React.useEffect(() => {
      const messages = processMessages((message) => {
         switch (message?.response?.ServiceId) {
            case OpcUa.DataTypeIds.CreateSessionResponse:
            case OpcUa.DataTypeIds.ActivateSessionResponse:
            case OpcUa.DataTypeIds.CloseSessionResponse:
               {
                  return true;
               }
         }
         return false;
      });
      messages?.forEach(message => {
         const response = message?.response;
         if (response?.ServiceId === OpcUa.DataTypeIds.CreateSessionResponse) {
            const csr = response.Body as OpcUa.CreateSessionResponse;
            m.current.authenticationToken = csr.AuthenticationToken;
            activateSession(csr);
         }
         else if (response?.ServiceId === OpcUa.DataTypeIds.ActivateSessionResponse) {
            const asr = response.Body as OpcUa.ActivateSessionResponse;
            m.current.serverNonce = asr.ServerNonce;
            m.current.sessionState = SessionState.SessionActive;
            setSessionState(m.current.sessionState);
         }
         else if (response?.ServiceId === OpcUa.DataTypeIds.CloseSessionResponse) {
            m.current.authenticationToken = undefined;
            m.current.serverNonce = undefined;
            m.current.sessionState = SessionState.NoSession;
            setSessionState(m.current.sessionState);
         }
      });
   }, [messageCounter, processMessages, activateSession])

   const setServerUrlImpl = React.useCallback((value: string) => {
      m.current.serverUrl = value ?? null;
      if (m.current.isEnabled) {
         setServerUrl(m.current.serverUrl);
      }
   }, []);

   const setIsEnabledImpl = React.useCallback((value: boolean) => {
      m.current.isEnabled = value;
      setIsEnabled(m.current.isEnabled);
   }, []);

   const setIsSessionEnabledImpl = React.useCallback((value: boolean) => {
      if (m.current.isSessionEnabled === value) {
         return;
      }
      m.current.isSessionEnabled = value;
      setIsSessionEnabled(m.current.isSessionEnabled);
      if (value && m.current.sessionState === SessionState.NoSession) {
         createSession();
         return;
      }
      if (!value && m.current.sessionState === SessionState.SessionActive) {
         deleteSession();
         return;
      }
   }, [createSession, deleteSession]);

   const sessionContext = {
      serverUrl: m.current.serverUrl,
      setServerUrl: setServerUrlImpl,
      isConnected: readyState === ReadyState.OPEN,
      sessionState,
      isEnabled: m.current.isEnabled,
      setIsEnabled: setIsEnabledImpl,
      isSessionEnabled,
      setIsSessionEnabled: setIsSessionEnabledImpl,
      requestTimeout: m.current.requestTimeout,
      setRequestTimeout: (value: number) => m.current.requestTimeout = value,
      sendRequest,
      messageCounter,
      processMessages
   } as ISessionContext;

   const processResponse = React.useCallback((response: IResponseMessage) => {
      if (response?.ServiceId === OpcUa.DataTypeIds.CreateSessionResponse) {
         const csr = response.Body as OpcUa.CreateSessionResponse;
         m.current.authenticationToken = csr.AuthenticationToken;
         activateSession(csr);
      }
      else if (response?.ServiceId === OpcUa.DataTypeIds.ActivateSessionResponse) {
         const asr = response.Body as OpcUa.ActivateSessionResponse;
         m.current.serverNonce = asr.ServerNonce;
         m.current.sessionState = SessionState.SessionActive;
         setSessionState(m.current.sessionState);
      }
      else if (response?.ServiceId === OpcUa.DataTypeIds.CloseSessionResponse) {
         m.current.authenticationToken = undefined;
         m.current.serverNonce = undefined;
         m.current.sessionState = SessionState.NoSession;
         setSessionState(m.current.sessionState);
      }
      else if (response) {
         processRawResponse(response);
      }
   }, [activateSession, processRawResponse])

   return (
      <SessionContext.Provider value={sessionContext}>
         {children}
      </SessionContext.Provider>
   );
};

export default SessionProvider;
