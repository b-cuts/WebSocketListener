﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace vtortola.WebSockets
{
    public class WebSocketHandshaker
    {
        readonly Dictionary<String, String> _headers;
        readonly SHA1 _sha1;
        private WebSocketEncodingExtensionCollection RequestExtensions;
        public WebSocketHttpRequest Request { get; private set; }
        readonly List<WebSocketExtension> _responseExtensions;
        public List<IWebSocketEncodingExtensionContext> NegotiatedExtensions { get; private set; }
        public Boolean IsWebSocketRequest
        {
            get
            {
                return _headers.ContainsKey("Host") &&
                       _headers.ContainsKey("Upgrade") && "websocket".Equals(_headers["Upgrade"], StringComparison.InvariantCultureIgnoreCase ) &&
                       _headers.ContainsKey("Connection") &&
                       _headers.ContainsKey("Sec-WebSocket-Key") && !String.IsNullOrWhiteSpace(_headers["Sec-WebSocket-Key"]) &&
                       _headers.ContainsKey("Sec-WebSocket-Version") && _headers["Sec-WebSocket-Version"] == "13";
            }
        }
        public WebSocketHandshaker(WebSocketEncodingExtensionCollection extensions)
        {
            _headers = new Dictionary<String, String>(StringComparer.InvariantCultureIgnoreCase);
            _sha1 = SHA1.Create();
            Request = new WebSocketHttpRequest();
            Request.Cookies = new CookieContainer();
            Request.Headers = new HttpHeadersCollection();
            RequestExtensions = extensions;
            _responseExtensions = new List<WebSocketExtension>();
            NegotiatedExtensions = new List<IWebSocketEncodingExtensionContext>();
        }

        public Boolean NegotiatesWebsocket(NetworkStream clientStream)
        {
            ReadHttpRequest(clientStream);

            ConsolidateObjectModel();

            if (IsWebSocketRequest)
                SelectExtensions();
            
            WriteHttpResponse(clientStream);
                        
            return IsWebSocketRequest;
        }

        private void SelectExtensions()
        {
            IWebSocketEncodingExtensionContext context;
            WebSocketExtension extensionResponse;
            foreach (var extRequest in Request.WebSocketExtensions)
            {
                var extension = RequestExtensions.SingleOrDefault(x => x.Name.Equals(extRequest.Name, StringComparison.InvariantCultureIgnoreCase));
                if (extension != null && extension.TryNegotiate(Request, out extensionResponse, out context))
                {
                    NegotiatedExtensions.Add(context);
                    _responseExtensions.Add(extensionResponse);
                }
            }
        }

        private void WriteHttpResponse(NetworkStream clientStream)
        {
            using (StreamWriter sw = new StreamWriter(clientStream, Encoding.ASCII, 1024, true))
            {
                if (!IsWebSocketRequest)
                {
                    SendNegotiationErrorResponse(sw);
                    sw.Flush();
                    clientStream.Close();
                }
                else
                {
                    SendNegotiationResponse(sw);
                }
            }
        }
        private void ReadHttpRequest(NetworkStream clientStream)
        {
            using (var sr = new StreamReader(clientStream, Encoding.ASCII, false, 1024, true))
            {
                String line = sr.ReadLine();

                ParseGET(line);

                while (!String.IsNullOrWhiteSpace(line))
                {
                    line = sr.ReadLine();
                    ParseHeader(line);
                }
            }
        }
        private void ParseGET(String line)
        {
            if (String.IsNullOrWhiteSpace(line) || !line.StartsWith("GET"))
                throw new WebSocketException("Not GET request");

            var parts = line.Split(' ');
            Request.RequestUri = new Uri(parts[1], UriKind.Relative);
            String version = parts[2];
            Request.HttpVersion = version.EndsWith("1.1") ? HttpVersion.Version11 : HttpVersion.Version10;
        }
        private void ParseHeader(String line)
        {
            var separator = line.IndexOf(":");
            if (separator == -1)
                return;
            String key = line.Substring(0, separator);
            String value = line.Substring(separator + 2, line.Length - (separator + 2));
            _headers.Add(key, value);
        }
        private void SendNegotiationResponse(StreamWriter sw)
        {
            sw.Write("HTTP/1.1 101 Switching Protocols\r\n");
            sw.Write("Upgrade: websocket\r\n");
            sw.Write("Connection: Upgrade\r\n");
            sw.Write("Sec-WebSocket-Accept: ");
            sw.Write(GenerateHandshake());
            
            if (_headers.ContainsKey("SEC-WEBSOCKET-PROTOCOL"))
            {
                sw.Write("\r\n");
                sw.Write("Sec-WebSocket-Protocol: ");
                sw.Write(_headers["SEC-WEBSOCKET-PROTOCOL"]);
            }

            if (_responseExtensions.Any())
            {
                Boolean firstExt=true, firstOpt=true;
                sw.Write("\r\n");
                sw.Write("Sec-WebSocket-Extensions: ");
                foreach (var extension in _responseExtensions)
                {
                    if(!firstExt)
                        sw.Write(",");

                    sw.Write(extension.Name);
                    var serverAcceptedOptions = extension.Options.Where(x => !x.ClientAvailableOption);
                    if(extension.Options.Any())
                    {
                        sw.Write(";");
                        foreach (var extOption in serverAcceptedOptions)
                        {
                            if(!firstOpt)
                                sw.Write(";");

                            sw.Write(extOption.Name);
                            if (extOption.Value != null)
                            {
                                sw.Write("=");
                                sw.Write(extOption.Value);
                            }
                            firstOpt = false;
                        }
                        firstExt = false;
                    }
                }
            }

            sw.Write("\r\n");
            sw.Write("\r\n");
        }
        private void SendNegotiationErrorResponse(StreamWriter sw)
        {
            sw.Write("HTTP/1.1 404 Bad Request\r\n");
            sw.Write("\r\n");
        }
        private String GenerateHandshake()
        {
            return Convert.ToBase64String(_sha1.ComputeHash(Encoding.UTF8.GetBytes(_headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        }
        private void ConsolidateObjectModel()
        {
            if(_headers.ContainsKey("Cookie"))
            {
                Request.Cookies.SetCookies(new Uri("http://" + _headers["Host"]), _headers["Cookie"]);
            }

            List<WebSocketExtension> extensionList = new List<WebSocketExtension>();
            if(_headers.ContainsKey("Sec-WebSocket-Extensions"))
            {
                var header = _headers["Sec-WebSocket-Extensions"];
                var extensions = header.Split(',');
                AssertArrayIsAtLeast(extensions, 2, "Cannot parse extension [" + header +"]");
                foreach (var extension in extensions)
                {
                    List<WebSocketExtensionOption> extOptions = new List<WebSocketExtensionOption>();
                    var parts = extension.Split(';');
                    AssertArrayIsAtLeast(extensions, 1, "Cannot parse extension [" + header + "]");
                    foreach (var part in parts.Skip(1))
                    {
                        var optParts = part.Split('=');
                        AssertArrayIsAtLeast(optParts, 1, "Cannot parse extension options [" + header + "]");
                        if(optParts.Length==1)
                            extOptions.Add(new WebSocketExtensionOption() { Name = optParts[0], ClientAvailableOption=true });
                        else
                            extOptions.Add(new WebSocketExtensionOption() { Name = optParts[0], Value = optParts[1]});
                    }
                    extensionList.Add(new WebSocketExtension(parts[0], extOptions));
                }
            }
            Request.SetExtensions(extensionList);
            Request.Headers = new HttpHeadersCollection();
            foreach (var kv in _headers)
                Request.Headers.Add(kv.Key, kv.Value);
        }
        private void AssertArrayIsAtLeast(String[] array, Int32 length, String error)
        {
            if (array == null || array.Length < length)
                throw new WebSocketException(error);
        }
    }
}
