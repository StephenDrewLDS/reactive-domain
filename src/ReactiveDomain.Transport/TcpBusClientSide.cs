﻿using System;
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
        public TcpBusClientSide(
            IDispatcher messageBus,
            EndPoint endpoint,
            ITcpConnection tcpConnection = null)
            : base(endpoint, messageBus)
        {
            TcpConnection.Add(tcpConnection ?? CreateTcpConnection(CommandEndpoint));
        }

        public TcpBusClientSide(
            IDispatcher messageBus,
            IPAddress hostIP,
            int commandPort,
            ITcpConnection tcpConnection = null)
            : base(hostIP, commandPort, messageBus)
        {

            TcpConnection.Add(tcpConnection ?? CreateTcpConnection(CommandEndpoint));
        }

        private ITcpConnection CreateTcpConnection(EndPoint endPoint)
        {
            Log.Info("TcpBusClientSide.CreateTcpConnection(" + endPoint + ") entered.");
            var clientTcpConnection = Transport.TcpConnection.CreateConnectingTcpConnection(Guid.NewGuid(),
                endPoint,
                new TcpClientConnector(),
                TimeSpan.FromSeconds(120),
                conn =>
                {
                    Log.Info("TcpBusClientSide.CreateTcpConnection(" + endPoint + ") successfully constructed TcpConnection.");

                    ConfigureTcpListener();
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


        private void ConfigureTcpListener()
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
