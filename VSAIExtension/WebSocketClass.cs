using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.DirectoryServices.ActiveDirectory;
using System.Windows;

public class WebSocketClass
{
    public System.Net.WebSockets.ClientWebSocket MyWebSocket = new System.Net.WebSockets.ClientWebSocket();
    private int RunningThread;
    private object Lock = new object();
    // Private tasks As New Queue(Of Action)
    private Queue<Task<object>> tasksWithParams = new Queue<Task<object>>();
    private int GCInterval = 0;
    private int Retries = 3;

    public const int maxMessageSize = 65536; // 8096 '1024 '65536

    public event DataReceivedEventHandler DataReceived;

    public delegate void DataReceivedEventHandler(string Data);

    public event DataBytesReceivedEventHandler DataBytesReceived;

    public delegate void DataBytesReceivedEventHandler(byte[] Data);

    private Task<object> CurrentlyRunningTask;

    string WebSocketURL = "ws://192.168.0.100:5000/ws/";
    private void Callback()
    {
        if (GCInterval % 1000 == 0)
            // System.Diagnostics.Debug.WriteLine("Cleaning up garbage mod 100")
            GC.Collect();
        GCInterval += 1;
        // If tasks.Count > 0 Then
        if (tasksWithParams.Count > 0)
        {
            // Dim Task As Action
            Task t;
            lock ((Lock))
            {
                // Task = tasks.Dequeue
                // System.Diagnostics.Debug.WriteLine(tasksWithParams.Count & " left in queue")
                t = tasksWithParams.Dequeue();
                CurrentlyRunningTask = (Task<object>)t;
                t.Start();
            }
        }
        else
        {
            System.Threading.Interlocked.Decrement(ref RunningThread);
            // System.Diagnostics.Debug.WriteLine("Cleaning up garbage queue finished")
            GC.Collect();
        }
    }
    private bool AddItemTOQueue(Task<object> t)
    {
        try
        {
            // Dim Task As New Action(AddressOf SomeWork)
            if (RunningThread < 1)
            {
                // Task.BeginInvoke(AddressOf Callback, MyArgument)
                CurrentlyRunningTask = t;
                t.Start();
                System.Threading.Interlocked.Increment(ref RunningThread);
            }
            else
                lock ((Lock))
                {
                    // tasks.Enqueue(Task)
                    tasksWithParams.Enqueue(t);
                    if (tasksWithParams.Count % 10 == 0)
                        Debug.WriteLine("Websocket Queue at " + tasksWithParams.Count);
                }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message + " " + ex.StackTrace);
        }
        return false;
    }

    public async Task<bool> AddMessageToWebSocketQueue(string Prefix, string Data, bool encodeinbase64 = false)
    {
        Task<object> t = new System.Threading.Tasks.Task<object>(() => SendDataToServerOneChunk(Prefix + Data, encodeinbase64));
        AddItemTOQueue(t);
        return true;
    }
    public async Task<bool> AddMessageToWebSocketQueue(string Data, bool encodeinbase64 = false)
    {
        Task<object> t = new System.Threading.Tasks.Task<object>(() => SendDataToServerOneChunk(Data, encodeinbase64));
        AddItemTOQueue(t);
        return true;
    }


    public bool StartMonitor()
    {
        System.Threading.Thread MyThread = new System.Threading.Thread(Monitor);
        MyThread.Start();
        return true;
    }

    private void Monitor()
    {
        while (true)
        {
            Debug.WriteLine("MyWebSocket " + MyWebSocket.State.ToString() + MyWebSocket.CloseStatus + MyWebSocket.CloseStatusDescription);
            System.Threading.Thread.Sleep(5000);
        }
    }

    public void UpdateGUI(string Message)
    {
        Debug.WriteLine(Message);
    }

    public System.Threading.Tasks.Task<bool> ConnectWebsocketUntilConnectedTask;
    public async Task<bool> ConnectWebSocket() // DesertComputerAgents.ClientEndPoint
    {
        if (MyWebSocket.State != System.Net.WebSockets.WebSocketState.Open & MyWebSocket.State != System.Net.WebSockets.WebSocketState.Connecting)
        {
            ConnectWebsocketUntilConnectedTask = ConnectWebsocketUntilConnected();
            UpdateGUI("ConnectWebsocketUntilConnectedTask result: " + ConnectWebsocketUntilConnectedTask.Result + Constants.vbCrLf);
            return ConnectWebsocketUntilConnectedTask.Result;
        }
        return true;
    }





    private byte[] receiveBuffer = new byte[maxMessageSize - 1 + 1];
    private string ReceivedData;
    private Task<bool> ConnectToWebServerAndReadTask;

    private async Task<bool> ConnectWebsocketUntilConnected()
    {
        int retryDelay = 5000; // 5 seconds
        int maxRetries = 3;
        int attempt = 0;

        while (attempt < maxRetries)
        {
            try
            {
                // Dispose and recreate WebSocket only if needed
                if (MyWebSocket != null)
                    MyWebSocket.Dispose();
                MyWebSocket = new System.Net.WebSockets.ClientWebSocket();

                Debug.WriteLine($"Attempt {attempt + 1} to connect WebSocket...");

                // Start connection task
                ConnectToWebServerAndReadTask = ConnectToWebServerAndRead(WebSocketURL, MyWebSocket);

                // Await the task instead of checking status manually
                bool isConnected = await ConnectToWebServerAndReadTask;

                if (isConnected)
                {
                    Debug.WriteLine("WebSocket connected successfully!");
                    return true; // Successfully connected
                }
                else
                    Debug.WriteLine("WebSocket connection failed, retrying...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ConnectWebsocketUntilConnected: " + ex.Message);
            }

            // Wait before retrying
            attempt += 1;
            await Task.Delay(retryDelay);
        }

        Debug.WriteLine("Unable to connect WebSocket after retries.");
        return false; // Failed after retries
    }

    private async Task<bool> ConnectWebsocketUntilConnectedOld() // Makes Websocket connection persistant
    {
        MyWebSocket.Dispose();
        MyWebSocket = null;
        MyWebSocket = new System.Net.WebSockets.ClientWebSocket();
        ConnectToWebServerAndReadTask = ConnectToWebServerAndRead(WebSocketURL, MyWebSocket);
        while (ConnectToWebServerAndReadTask.Status != TaskStatus.RanToCompletion) // And MyTask.Status <> TaskStatus.WaitingForActivation
        {
            try
            {
                //if (Application.MessageLoop)
                    //Application.DoEvents();// This is required to work
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ConnectWebsocketUntilConnected: " + ex.Message);
            }
        }

        while (!ConnectToWebServerAndReadTask.Result == true | Retries == 0)
        {
            MyWebSocket.Dispose();
            MyWebSocket = null;
            MyWebSocket = new System.Net.WebSockets.ClientWebSocket();
            ConnectToWebServerAndReadTask = ConnectToWebServerAndRead(WebSocketURL, MyWebSocket);

            while (ConnectToWebServerAndReadTask.Status != TaskStatus.RanToCompletion) // And MyTask.Status <> TaskStatus.WaitingForActivation
            {
                try
                {
                    //if (Application.MessageLoop)
                        //Application.DoEvents();// This is required to work
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ConnectWebsocketUntilConnected: " + ex.Message);
                }
            }

            Debug.WriteLine(ConnectToWebServerAndReadTask.Result);
            System.Threading.Thread.Sleep(5000);
            Retries -= 1;
        }

        Debug.WriteLine("Unable to connect Websocket after retries");

        if (Retries == 0)
            return false;
        else
            return true;
    }


    private async Task<bool> ConnectToWebServerAndRead(String URL, System.Net.WebSockets.ClientWebSocket MySocket)
    {
        try
        {


            // Set butter is tied to chucksize in the filexfer
            MySocket.Options.SetBuffer(maxMessageSize, maxMessageSize); // Myfiles are bigger.
            CancellationTokenSource myToken = new CancellationTokenSource(5000);
            System.Net.ServicePointManager.ServerCertificateValidationCallback = (s, c, h, d) => true;
            // socket.Options.SetRequestHeader("User-Agenadt", "DCAConsoleAgent") 'Does not work for specific headers

            UpdateGUI("Connecting to: " + URL);

            await MySocket.ConnectAsync(new Uri(URL), myToken.Token);

            //while (MySocket.State == WebSocketState.Open)
                //Application.DoEvents();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("WebSocket Connect Ex: " + ex.Message);
            return false;
        }

        UpdateGUI("ConnectToWebServerAndRead: Websocket Connected to: " + URL);
        //AddMessageToWebSocketQueue("@init");

        int count = 0;
        WebSocketReceiveResult receiveResult;
        while (MySocket.State == WebSocketState.Open)
        {
            Retries = 10;
            try
            {
                Array.Clear(receiveBuffer, 0, receiveBuffer.Length);
                UpdateGUI("Waiting for incoming data");
                receiveResult = await MySocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer, 0, receiveBuffer.Length - 1), CancellationToken.None);
                UpdateGUI("Data received");
                count = receiveResult.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Recv Ex:" + ex.Message + Constants.vbCrLf + ex.InnerException.Message);
                return false;
            }

            try
            {
                var receivedString = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, count);
                var echoString = "You said " + receivedString;
                ArraySegment<byte> outputBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(echoString));
                // Await socket.SendAsync(outputBuffer, WebSocketMessageType.Text, True, CancellationToken.None)
                // Debug.WriteLine("I Recv: " & System.Text.ASCIIEncoding.ASCII.GetString(receiveBuffer))

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                    await MySocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                    await MySocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Cannot accept binary frame", CancellationToken.None);
                else
                {
                    count = receiveResult.Count;

                    while (receiveResult.EndOfMessage == false)
                    {
                        if (count >= maxMessageSize)
                        {
                            try
                            {
                                string closeMessage = string.Format("Maximum message size: {0} bytes.", maxMessageSize);
                                await MySocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, closeMessage, CancellationToken.None);
                                UpdateGUI("Maxed");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine(ex.Message);
                            }
                            return false;
                        }

                        try
                        {
                            receiveResult = await MySocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer, count, maxMessageSize - count), CancellationToken.None);
                            count += receiveResult.Count;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
                    }
                    ReceivedData = System.Text.ASCIIEncoding.ASCII.GetString(receiveBuffer, 0, count); // Add some way to Idenify the Source client and also make sure the other clients and send request.
                    UpdateGUI("ConnectToWebServerAndRead:" + ReceivedData);

                    DataBytesReceived?.Invoke(receiveBuffer);
                    DataReceived?.Invoke(ReceivedData);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ConnectToWebServerAndRead: " + ex.Message);
            }
        }
        UpdateGUI("Websocket closed");
        return false;
    }
    private async Task SendDataToServerInChunks(string Prefix, string Cmd, bool encodeinbase64 = false) // This may be retired
    {
        try
        {
            // UpdateGUI("SendDataToServerInChunks: " & Prefix & " " & Cmd & vbCrLf)
            if (MyWebSocket.State == WebSocketState.Open)
            {
                ArraySegment<byte> outputBuffer;
                int chunkSize = maxMessageSize - Prefix.Length; // This may require to be 1024 and under MTU 1500
                if (Cmd.Length > chunkSize)
                {
                    for (var i = 0; i <= Cmd.Length; i += chunkSize)
                    {
                        if (i + chunkSize > Cmd.Length)
                            chunkSize = Cmd.Length - i;

                        if (encodeinbase64)
                            outputBuffer = new ArraySegment<byte>(Convert.FromBase64String(Prefix + Cmd.Substring(i, chunkSize))); // Not tested yet
                        else
                            outputBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(Prefix + Cmd.Substring(i, chunkSize)));

                        await MyWebSocket.SendAsync(outputBuffer, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                else
                {
                    if (encodeinbase64)
                        outputBuffer = new ArraySegment<byte>(Convert.FromBase64String(Prefix + Cmd)); // Not tested yet
                    else
                        outputBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(Prefix + Cmd));

                    await MyWebSocket.SendAsync(outputBuffer, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            UpdateGUI("Exception: SendDataToServerInChunks(" + Prefix + " As String, " + Cmd + " As String, Optional " + encodeinbase64 + " As Boolean = False)" + " =>" + ex.Message + Constants.vbCrLf);
            Debug.WriteLine("Exception: SendDataToServerInChunks(" + Prefix + " As String, " + Cmd + " As String, Optional " + encodeinbase64 + " As Boolean = False)" + " =>" + ex.Message);
        }
    }

    private async Task SendDataToServerOneChunk(string Prefix, string Cmd, bool encodeinbase64 = false) // These should never be called directly and only queued
    {
        try
        {
            //AddMessageToWebSocketQueue(Prefix + Cmd, encodeinbase64);
            await SendDataToServerOneChunk(Prefix + Cmd, encodeinbase64);
        }
        catch (Exception ex)
        {
            UpdateGUI("Exception: Form1.WebSocketClient.AddMessageToWebSocketQueue(" + Prefix + " As String, " + Cmd + " As String, Optional " + encodeinbase64 + " As Boolean = False)" + " =>" + ex.Message + Constants.vbCrLf);
            Debug.WriteLine("Exception: SendDataToServerOneChunk(" + Prefix + " As String, " + Cmd + " As String, Optional " + encodeinbase64 + " As Boolean = False)" + " =>" + ex.Message);
        }
    }

    private async Task SendDataToServerOneChunk(string Cmd, bool encodeinbase64 = false) // Be aware of timing issues when logging, websockets are timesensitive!!
    {
        try
        {
            // Debug.WriteLine("SendDataToServerOneChunk: " & Cmd & vbCrLf)
            UpdateGUI("SendDataToServerOneChunk: " + Cmd);
            if (MyWebSocket.State == WebSocketState.Open)
            {
                ArraySegment<byte> outputBuffer;

                if (encodeinbase64)
                    outputBuffer = new ArraySegment<byte>(Convert.FromBase64String(Cmd)); // Not tested yet
                else
                    outputBuffer = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(Cmd));

                // Await MyWebSocket.SendAsync(outputBuffer, WebSocketMessageType.Text, True, CancellationToken.None)
                // UpdateGUI("SendDataToServerOneChunk completed" & vbCrLf)
                using (System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(5000)) // 5000 ms = 5 seconds
                {
                    try
                    {
                        // Await MyWebSocket.SendAsync(outputBuffer, WebSocketMessageType.Text, True, cts.Token)


                        Task sendTask = MyWebSocket.SendAsync(outputBuffer, WebSocketMessageType.Text, true, cts.Token);
                        Task timeoutTask = Task.Delay(5000); // 5-second timeout

                        // Wait for either the send operation or the timeout
                        Task completedTask = await Task.WhenAny(sendTask, timeoutTask);
                        if (completedTask == timeoutTask)
                        {
                            // Timeout occurred
                            Debug.WriteLine("SendAsync timed out after 5 seconds.");
                            UpdateGUI("Error: SendAsync timed out after 5 seconds.");
                            cts.Cancel(); // Cancel the WebSocket operation if possible
                        }
                        else
                        {
                            // The send operation completed successfully
                            await sendTask;
                            UpdateGUI("SendDataToServerOneChunk completed");
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        Debug.WriteLine("SendAsync timed out after 5 seconds.");
                        UpdateGUI("Error: SendAsync timed out after 5 seconds.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Exception: SendDataToServerOneChunk(" + Cmd + " As String, Optional " + encodeinbase64 + " As Boolean = False)" + " =>" + ex.Message);
        }
        Callback();
    }
}
