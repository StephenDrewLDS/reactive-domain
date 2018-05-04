using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ReactiveDomain.Transport.Framing;
using ReactiveDomain.Messaging.Bus;

namespace ReactiveDomain.Transport
{
    public class TcpBusClientSide : TcpBusSide
    {
        private readonly bool _ownsConnection;

        [Obsolete("The dispatcher is not used")]
        public TcpBusClientSide(
            IDispatcher messageBus,
            IPAddress hostIP,
            int commandPort,
            ITcpConnection tcpConnection = null)
            : this(hostIP, commandPort, tcpConnection)
        {
        }

        public TcpBusClientSide(
            IPAddress hostIP,
            int commandPort,
            ITcpConnection tcpConnection = null)
            : base(hostIP, commandPort)
        {
            _ownsConnection = tcpConnection == null;
            TcpConnection.Add(tcpConnection ?? CreateTcpConnection(CommandEndpoint));
        }

        protected override void Dispose(bool disposing)
        {
            if (_ownsConnection)
            {
                // Should only be one...
                foreach (var conn in TcpConnection)
                {
                    try
                    {
                        conn?.Close("Client shutting down");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Error closing TCP connection {0}", ex.Message);
                    }
                }
            }
        }

        private ITcpConnection CreateTcpConnection(IPEndPoint endPoint)
        {
            Log.Info("TcpBusClientSide.CreateTcpConnection(" + endPoint.Address + ":" + endPoint.Port + ") entered.");
            var clientTcpConnection = Transport.TcpConnection.CreateConnectingTcpConnection(Guid.NewGuid(),
                endPoint,
                new TcpClientConnector(),
                TimeSpan.FromSeconds(120),
                conn =>
                {
                    Log.Info("TcpBusClientSide.CreateTcpConnection(" + endPoint.Address + ":" + endPoint.Port + ") successfully constructed TcpConnection.");

                    ConfigureTcpConnection();
                },
                (conn, err) =>
                {
                    HandleError(conn, err);
                },
                verbose: true);

            return clientTcpConnection;
        }
        
        private void HandleError(ITcpConnection conn, SocketError err)
        {
            // assume that any connection error means that the Host isn't running, yet.  Just wait
            // a second and try again.
            TcpConnection.Clear(); //client should only have one connection
            Thread.Sleep(1000);
            Log.Debug("TcpBusClientSide call to CreateConnectingTcpConnection() failed - SocketError= " + err + " - retrying.");
            TcpConnection.Add(CreateTcpConnection(CommandEndpoint));
        }


        private void ConfigureTcpConnection()
        {
            Framer.RegisterMessageArrivedCallback(TcpMessageArrived);
            Action<ITcpConnection, IEnumerable<ArraySegment<byte>>> callback = null;
            callback = (x, data) =>
            {
                try
                {
                    Framer.UnFrameData(data);
                }
                catch (PackageFramingException exc)
                {
                    Log.ErrorException(exc, "LengthPrefixMessageFramer.UnFrameData() threw an exception:");
                    // SendBadRequestAndClose(Guid.Empty, string.Format("Invalid TCP frame received. Error: {0}.", exc.Message));
                    return;
                }
                TcpConnection[0].ReceiveAsync(callback); //client should only have one connection
            };
            TcpConnection[0].ReceiveAsync(callback); //client should only have one connection
        }

    }
}
