using System;
using System.Collections.Generic;
using System.Net;
using ReactiveDomain.Messaging.Bus;
using ReactiveDomain.Transport.Framing;

namespace ReactiveDomain.Transport
{
    public class TcpBusServerSide : TcpBusSide
    {
        private readonly TcpServerListener _commandPortListener;

        [Obsolete("The dispatcher is not used")]
        public TcpBusServerSide(
            IPAddress hostIp,
            int commandPort,
            IDispatcher messageBus)
            : this(hostIp, commandPort)
        { }

        public TcpBusServerSide(
            IPAddress hostIp,
            int commandPort)
            : base(hostIp, commandPort)
        {
           
            Log.Info("ConfigureTcpListener(" + CommandEndpoint.AddressFamily + ", " + CommandEndpoint.Port + ") entered.");
            
            var listener = new TcpServerListener(CommandEndpoint);

            listener.StartListening((endPoint, socket) =>
            {
               var conn = Transport.TcpConnection.CreateAcceptedTcpConnection(Guid.NewGuid(), endPoint, socket, verbose: true);

                Action<ITcpConnection, IEnumerable<ArraySegment<byte>>> callback = null;
                callback = (x, data) =>
                {
                    LengthPrefixMessageFramer framer = new LengthPrefixMessageFramer();
                    framer.RegisterMessageArrivedCallback(TcpMessageArrived);
                    try
                    {
                        framer.UnFrameData(data);
                    }
                    catch (PackageFramingException exc)
                    {
                        Log.ErrorException(exc, "LengthPrefixMessageFramer.UnFrameData() threw an exception:");
                        // SendBadRequestAndClose(Guid.Empty, string.Format("Invalid TCP frame received. Error: {0}.", exc.Message));
                        return;
                    }
                    conn.ReceiveAsync(callback);
                };
                conn.ReceiveAsync(callback);
                TcpConnection.Add(conn);
            }, "Standard");
            Log.Info("ConfigureTcpListener(" + CommandEndpoint.AddressFamily + ", " + CommandEndpoint.Port + ") successfully constructed TcpServerListener.");
            _commandPortListener = listener;
        }

        protected override void Dispose(bool disposing)
        {
            _commandPortListener.Stop();

            foreach (var conn in TcpConnection)
            {
                try
                {
                    conn?.Close("Server shutting down");
                }
                catch (Exception ex)
                {
                    Log.Error("Error closing TCP connection {0}", ex.Message);
                }
            }
        }
    }
}
