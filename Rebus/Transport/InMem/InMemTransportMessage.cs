﻿using System.Collections.Generic;
using System.IO;
using Rebus.Messages;

namespace Rebus.Transport.InMem
{
    public class InMemTransportMessage
    {
        public InMemTransportMessage(TransportMessage transportMessage)
        {
            Headers = transportMessage.Headers;
            Body = new byte[transportMessage.Body.Length];
            transportMessage.Body.Read(Body, 0, Body.Length);
        }

        public Dictionary<string,string> Headers { get; private set; }
            
        public byte[] Body { get; private set; }

        public TransportMessage ToTransportMessage()
        {
            return new TransportMessage(Headers, new MemoryStream(Body));
        }
    }
}