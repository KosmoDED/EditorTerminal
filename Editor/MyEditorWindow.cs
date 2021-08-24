using System.Collections;
using UnityEngine;
using Unity.EditorCoroutines.Editor;
using UnityEditor;


using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using gw.proto.http;
using gw.proto.utils;
using gw.unium;

using Debug = UnityEngine.Debug;

public class MyEditorWindow : EditorWindow
{

    [MenuItem("Tools/EditorTerminal/Server")]
    private static void ShowWindow() {
        var window = GetWindow<MyEditorWindow>();
        window.titleContent = new GUIContent("EditorTerminalServer");
        window.Show(); 
    }
    
    void OnEnable()
    {
        Debug.Log("On Enable tool");
        EditorCoroutineUtility.StartCoroutine(UpdateUnium(), this);

        if( AutoStart )
        {
            StartServer();
        }

        UnityEngine.Debug.Log("Editor Unium Component");

        Unium.RoutesHTTP.Add("/start",StartHandler);
        Unium.RoutesHTTP.Add("/refresh",RefreshHandler);
    }

    private void StartHandler(RequestAdapter req, string path)
    {
        Debug.Log($"file name: {GUIUtility.systemCopyBuffer}");
        req.Respond($"started {GUIUtility.systemCopyBuffer}");
    }

    private void RefreshHandler(RequestAdapter req, string path)
    {
        req.Respond("Refresh start");
        AssetDatabase.Refresh();
        req.Respond("Refreshed");
    }

    int m_Updates = 0;

    IEnumerator UpdateUnium()
    {
        while (true)
        {
            UpdateLogic();
            yield return null;
        }
    }

    public static MyEditorWindow Singleton = null;
    public static bool IsDebug { get { return Singleton != null && Singleton.EnableDebug; } }

    public int      Port            = 8342;
    public bool     RunInBackground = true;
    public bool     EnableDebug     = false;
    public bool     AutoStart       = true;
    public string   StaticFiles     = null;

    public enum AddressStrategy
    {
        AllInterfaces,
        PublicIp,
        PublicIpInPlayer,
    }
    public AddressStrategy ServeOn = AddressStrategy.AllInterfaces;

#if !UNIUM_DISABLE && ( DEVELOPMENT_BUILD || UNITY_EDITOR || UNIUM_ENABLE )

    //----------------------------------------------------------------------------------------------------

    class GameThreadRequest
    {
        public RequestAdapterHTTP   Request;
        public Route                Route;
    }

    List< GameThreadRequest >   mQueuedRequests = new List<GameThreadRequest>();
    List< UniumSocket >         mSockets        = new List<UniumSocket>();
    Server                      mServer         = null;


    //----------------------------------------------------------------------------------------------------

    void Awake()
    {
        // there can be only one!

        if( Singleton != null && Singleton != this )
        {
            Destroy( this );
            return;
        }

        // and always one

        // DontDestroyOnLoad( transform.gameObject );
        Singleton = this;

        // keep game running when editor does not have focus - needed as we need to dispatch on the game thread (which would be paused otherwise)

        if( RunInBackground )
        {
            Application.runInBackground = true;
        }

        var root = Path.Combine( Application.streamingAssetsPath, StaticFiles != null ? StaticFiles : "" );

        HandlerFile.Mount( "persistent", Application.persistentDataPath );
        HandlerFile.Mount( "streaming",  Application.streamingAssetsPath );
        HandlerFile.Mount( "root",       root );


        Application.logMessageReceivedThreaded += HandlerUtils.LogMessage;
        Events.Singleton.BindEvents();
    }


    ////////////////////////////////////////////////////////////////////////////////////////////////////

    // void OnEnable()
    // {
        
    // }


    //----------------------------------------------------------------------------------------------------

    void OnDisable()
    {
        if( Singleton == this )
        {
            Application.logMessageReceivedThreaded -= HandlerUtils.LogMessage;
            Events.Singleton.UnbindEvents();

            StopServer();
            Singleton = null;
        }
    }


    //----------------------------------------------------------------------------------------------------

    public void StartServer()
    {
        // start server

        mServer = new Server();

        if( ServeOn == AddressStrategy.PublicIp || (ServeOn == AddressStrategy.PublicIpInPlayer && !Application.isEditor) )
        {
            mServer.Settings.Address = IPAddress.Parse( Util.DetectPublicIPAddress() );
        }

        mServer.Settings.Port = Port;

        mServer.Dispatcher.OnWebRequest     = OnWebRequest;
        mServer.Dispatcher.OnSocketRequest  = OnSocketOpen;
        mServer.Dispatcher.OnSocketClose += OnWebSocketClose;

        mServer.Start();

        Log( string.Format( "server listening on {0}:{1}", mServer.Settings.Address.ToString(), Port ) );
    }


    //----------------------------------------------------------------------------------------------------

    public void StopServer()
    {
        if( mServer == null )
        {
            return;
        }

        Log( "server stopped" );

        mServer.Stop();
        mServer.Dispatcher.OnSocketClose -= OnWebSocketClose;
        mServer = null;

        lock( mQueuedRequests )
        {
            foreach( var req in mQueuedRequests )
            {
                req.Request.Reject( ResponseCode.InternalServerError );
            }

            mQueuedRequests.Clear();
        }

        lock( mSockets )
        {
            mSockets.Clear();
        }
    }


    //----------------------------------------------------------------------------------------------------

    void LateUpdate()
    {
        ProcessWebRequestsOnGameThread();
        ProcessSockets();
    }

    public void UpdateLogic()
    {
        ProcessWebRequestsOnGameThread();
        ProcessSockets();
    }


    
    void OnDrawGizmos()
   {
      // Your gizmo drawing thing goes here if required...
 
#if UNITY_EDITOR
      // Ensure continuous Update calls.
      if (!Application.isPlaying)
      {
         UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
         UnityEditor.SceneView.RepaintAll();
      }
#endif
   }


    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Note that OnWebRequest is executed on a worker thread

    void OnWebRequest( HttpRequest req )
    {
        Log( req.URL );

        if( string.IsNullOrEmpty( req.URL ) )
        {
            req.Reject( ResponseCode.NotFound );
            return;
        }


        // set default headers

        req.Response.Headers[ "Access-Control-Allow-Origin" ] = "*";
        req.Response.Headers[ "Cache-Control" ] = "no-store, must-revalidate";
        req.Response.Headers[ "Content-Type" ]  = "application/json";


        // handle request

        var route = Unium.RoutesHTTP.Find( req.URL );

        if( route == null || route.Handler == null )
        {
            req.Reject( ResponseCode.NotFound );
            return;
        }

        if( route.DispatchOnGameThread )
        {
            lock( mQueuedRequests )
            {
                mQueuedRequests.Add( new GameThreadRequest() { Request = new RequestAdapterHTTP( req ), Route = route } );
            }
        }
        else
        {
            route.Dispatch( new RequestAdapterHTTP( req ) );
        }
    }


    //----------------------------------------------------------------------------------------------------

    void ProcessWebRequestsOnGameThread()
    {
        // process all pending requests on the game thread
        lock( mQueuedRequests )
        {
            if( mQueuedRequests.Count == 0 )
            {
                return;
            }

            // dispatch one per frame

            var req = mQueuedRequests[ 0 ];

            req.Route.Dispatch( req.Request );
            mQueuedRequests.RemoveAt( 0 );
        }
    }


    ////////////////////////////////////////////////////////////////////////////////////////////////////

    void OnSocketOpen( WebSocket ws )
    {
        if( ws.URL != "/ws" )
        {
            Log( string.Format( "[sock:{0}] rejected - {1}", ws.ID, ws.URL ) );
            ws.Reject( ResponseCode.NotFound );
            return;
        }

        lock( mSockets )
        {
            Log( string.Format( "[sock:{0}] connected - {1}", ws.ID, ws.URL ) );
            mSockets.Add( new UniumSocket( ws ) );
            ws.Accept();
        }
    }


    //----------------------------------------------------------------------------------------------------

    void OnWebSocketClose( WebSocket ws )
    {
        lock( mSockets )
        {
            Log( string.Format( "[sock:{0}] closed", ws.ID ) );

            var socket = ws.User as UniumSocket;
            socket.OnClose();
            mSockets.Remove( socket );
        }
    }


    //----------------------------------------------------------------------------------------------------

    void ProcessSockets()
    {
        lock( mSockets )
        {
            foreach( var ws in mSockets )
            {
                ws.Tick();
            }
        }
    }

#endif

    ////////////////////////////////////////////////////////////////////////////////////////////////////

    static int sLogNumber = 0;

    [Conditional( "DEBUG" )]
    public static void Log( string msg, LogType type = LogType.Log )
    {
        if( Singleton == null || Singleton.EnableDebug == false )
        {
            return;
        }

        var str = string.Format( "[unium][{0}] {1}", sLogNumber++, msg );

        switch( type )
        {
            case LogType.Log:       UnityEngine.Debug.Log( str ); break;
            case LogType.Warning:   UnityEngine.Debug.LogWarning( str ); break;
            default:
            case LogType.Error:     UnityEngine.Debug.LogError( str ); break;
        }
    }

    [Conditional( "DEBUG" )]
    public static void Warn( string msg )
    {
        Log( msg, LogType.Warning );
    }

    [Conditional( "DEBUG" )]
    public static void Error( string msg )
    {
        Log( msg, LogType.Error );
    }
}