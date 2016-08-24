﻿using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Web;
using System.Collections.Specialized;
using LiteDB;

namespace pnxmono
{

    public class HttpProcessor
    {
        public TcpClient socket;
        public HttpServer srv;
        private Stream inputStream;
        public StreamWriter outputStream;
        public string http_method;
        public string http_url;
        public string http_protocol_versionstring;
        public Hashtable httpHeaders = new Hashtable();

        private static int MAX_POST_SIZE = 10 * 1024 * 1024; // 10MB
        public HttpProcessor(TcpClient s, HttpServer srv)
        {
            socket = s;
            this.srv = srv;
        }
        private string streamReadLine(Stream inputStream)
        {
            int next_char;
            string thisData = "";
            while (true)
            {
                next_char = inputStream.ReadByte();
                if (next_char == '\n') { break; }
                if (next_char == '\r') { continue; }
                if (next_char == -1) { Thread.Sleep(1); continue; }
                thisData += Convert.ToChar(next_char);
            }
            return thisData;
        }
        public void process()
        {
            // we can't use a StreamReader for input, because it buffers up extra data on us inside it's
            // "processed" view of the world, and we want the data raw after the headers
            inputStream = new BufferedStream(socket.GetStream());

            outputStream = new StreamWriter(new BufferedStream(socket.GetStream()));
            try
            {
                parseRequest();
                readHeaders();
                if (http_method.Equals("GET"))
                {
                    handleGETRequest();
                }
                else if (http_method.Equals("POST"))
                {
                    handlePOSTRequest();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.ToString());
                writeFailure();
            }
            outputStream.Flush();
            // bs.Flush(); // flush any remaining output
            inputStream = null; outputStream = null; // bs = null;            
            socket.Close();
        }
        public void parseRequest()
        {
            String request = streamReadLine(inputStream);
            string[] tokens = request.Split(' ');
            if (tokens.Length != 3)
            {
                throw new Exception("invalid http request line");
            }
            http_method = tokens[0].ToUpper();
            http_url = tokens[1];
            http_protocol_versionstring = tokens[2];
            //Console.WriteLine("starting: " + request);
        }
        public void readHeaders()
        {
           // Console.WriteLine("readHeaders()");
            String line;
            while ((line = streamReadLine(inputStream)) != null)
            {
                if (line.Equals(""))
                {
                  //  Console.WriteLine("got headers");
                    return;
                }
                int separator = line.IndexOf(':');
                if (separator == -1)
                {
                    throw new Exception("invalid http header line: " + line);
                }
                String name = line.Substring(0, separator);
                int pos = separator + 1;
                while ((pos < line.Length) && (line[pos] == ' '))
                {
                    pos++; // strip any spaces
                }
                string value = line.Substring(pos, line.Length - pos);
               // Console.WriteLine("header: {0}:{1}", name, value);
                httpHeaders[name] = value;
            }
        }
        public void handleGETRequest()
        {
            srv.handleGETRequest(this);
        }
        private const int BUF_SIZE = 4096;
        public void handlePOSTRequest()
        {
            // this post data processing just reads everything into a memory stream.
            // this is fine for smallish things, but for large stuff we should really
            // hand an input stream to the request processor. However, the input stream 
            // we hand him needs to let him see the "end of the stream" at this content 
            // length, because otherwise he won't know when he's seen it all! 
          //  Console.WriteLine("get post data start");
            int content_len = 0;
            MemoryStream ms = new MemoryStream();
            if (httpHeaders.ContainsKey("Content-Length"))
            {
                content_len = Convert.ToInt32(httpHeaders["Content-Length"]);
                if (content_len > MAX_POST_SIZE)
                {
                    throw new Exception(
                        String.Format("POST Content-Length({0}) too big for this simple server",
                          content_len));
                }
                byte[] buf = new byte[BUF_SIZE];
                int to_read = content_len;
                while (to_read > 0)
                {
                   // Console.WriteLine("starting Read, to_read={0}", to_read);
                    int numread = inputStream.Read(buf, 0, Math.Min(BUF_SIZE, to_read));
                 //   Console.WriteLine("read finished, numread={0}", numread);
                    if (numread == 0)
                    {
                        if (to_read == 0)
                        {
                            break;
                        }
                        else
                        {
                            throw new Exception("client disconnected during post");
                        }
                    }
                    to_read -= numread;
                    ms.Write(buf, 0, numread);
                }
                ms.Seek(0, SeekOrigin.Begin);
            }
           // Console.WriteLine("get post data end");
            srv.handlePOSTRequest(this, new StreamReader(ms));
        }
        public void writeSuccess(string content_type = "text/html")
        {
            // this is the successful HTTP response line
            outputStream.WriteLine("HTTP/1.0 200 OK");
            // these are the HTTP headers...          
            outputStream.WriteLine("Content-Type: " + content_type);
            outputStream.WriteLine("Connection: close");
            // ..add your own headers here if you like
            outputStream.WriteLine(""); // this terminates the HTTP headers.. everything after this is HTTP body..
        }
        public void writeFailure()
        {
            // this is an http 404 failure response
            outputStream.WriteLine("HTTP/1.0 404 File not found");
            // these are the HTTP headers
            outputStream.WriteLine("Connection: close");
            // ..add your own headers here
            outputStream.WriteLine(""); // this terminates the HTTP headers.
        }
    }
    public abstract class HttpServer
    {
        protected int port;
        TcpListener listener;
        bool is_active = true;
        public HttpServer(int port)
        {
            this.port = port;
        }
        public void listen()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            while (is_active)
            {
                TcpClient s = listener.AcceptTcpClient();
                HttpProcessor processor = new HttpProcessor(s, this);
                Thread thread = new Thread(new ThreadStart(processor.process));
                thread.Start();
                Thread.Sleep(1);
            }
        }
        public abstract void handleGETRequest(HttpProcessor p);
        public abstract void handlePOSTRequest(HttpProcessor p, StreamReader inputData);
    }
    public class MyHttpServer : HttpServer
    {
        public static string useVoiceChecked = "off";
        public static string useLocalCTChecked = "off";
        public static string useCTChecked = "off";
        public static ConfigData myData = new ConfigData();

        public MyHttpServer(int port)
            : base(port)
        {
        }
        public override void handleGETRequest(HttpProcessor p)
        {
            if (p.http_url.Equals("/Test.png"))
            {
                Stream fs = File.Open("../../Test.png", FileMode.Open);
                p.writeSuccess("image/png");
                fs.CopyTo(p.outputStream.BaseStream);
                p.outputStream.BaseStream.Flush();
            }

            // Read local db to show current values on webpage.

            ConfigData status = MainClass.getDBData();
            
            MainClass.useVoicePrompts = status.useVoicePrompts;
            MainClass.defTalkgroup = status.defaultTG;
            MainClass.defTimeout = status.defaultTimeout;
            MainClass.useCT = status.useCT;
            MainClass.useLocalCT = status.useLocalCT;
            useCTChecked = "";
            useLocalCTChecked = "";
            useVoiceChecked = "";
            if (MainClass.useCT) useCTChecked = "checked";
            if (MainClass.useLocalCT) useLocalCTChecked = "checked";
            if (MainClass.useVoicePrompts) useVoiceChecked = "checked";

           // Console.WriteLine("request: {0}", p.http_url);
            p.writeSuccess();
            p.outputStream.WriteLine("<html xmlns = 'http://www.w3.org/1999/xhtml' dir='ltr' lang='en' id='vbulletin_html'>");
            p.outputStream.WriteLine("<head>");
            p.outputStream.WriteLine("<meta http-equiv='Content-Type' content = 'text/html; charset=ISO-8859-1' />");
            p.outputStream.WriteLine("<html><body><h1 style='color:blue;margin-left:30px;'>P25NX Local Config</h1>");
            p.outputStream.WriteLine("<form method=post action=/form>");
            p.outputStream.WriteLine("Default TalkGroup: <input type=text name=deftg value=" + MainClass.defaultTalkGroup + "  >");
            p.outputStream.WriteLine("<br>Default TG Timeout: <input type=text name=defto value=" + MainClass.defTimeout.ToString() + ">");
            p.outputStream.WriteLine("<br>");
            p.outputStream.WriteLine("Use Voice Prompts: <input type=checkbox name=voiceprompts " + useVoiceChecked + " ><BR>");
            p.outputStream.WriteLine("Use Remote Courtesy Tone: <input type=checkbox name=ctone " + useCTChecked + " ><BR>");
            p.outputStream.WriteLine("Use Local Courtesy Tone: <input type=checkbox name=lctone " + useLocalCTChecked + " ><BR>");
            p.outputStream.WriteLine("<br><HR><input type=submit>");
            p.outputStream.WriteLine("</form>");
        }
        public override void handlePOSTRequest(HttpProcessor p, StreamReader inputData)
        {
           // Console.WriteLine("POST request: {0}", p.http_url);
            string thisData = inputData.ReadToEnd();
            p.writeSuccess();
            p.outputStream.WriteLine("<html><body><h1 style='color:blue;margin-left:30px;'>P25NX Local Config Result</h1>");
            p.outputStream.WriteLine("<html><body><h2 style='color:blue;margin-left:30px;'>Config Saved.</h2>");
            p.outputStream.WriteLine("<a href=/test>return</a><p>");
            // p.outputStream.WriteLine("postbody: <pre>{0}</pre>", thisData);
            NameValueCollection qscoll = HttpUtility.ParseQueryString(thisData);

            myData = new ConfigData();

            myData.Id = 1;

            myData.defaultTG = qscoll["deftg"];
            myData.defaultTimeout = Int32.Parse(qscoll["defto"]);
            if (qscoll["voiceprompts"] == "on")
            {
                myData.useVoicePrompts = true;
            }
            else
                myData.useVoicePrompts = false;

            if (qscoll["ctone"] == "on")
            {
                myData.useCT = true;
            }
            else
                myData.useCT = false;

            if (qscoll["lctone"] == "on")
            {
                myData.useLocalCT = true;
            }
            else
                myData.useLocalCT = false;


            using (var db = new LiteDatabase("MyData.db"))
            {
                var configs = db.GetCollection<ConfigData>("config");
                configs.Update(myData);
            }


            //update local variables
            MainClass.useVoicePrompts = myData.useVoicePrompts;
            MainClass.defTimeout = myData.defaultTimeout;
            MainClass.useCT = myData.useCT;
            MainClass.defaultTalkGroup = Int32.Parse(myData.defaultTG);
            MainClass.useLocalCT = myData.useLocalCT;



        }
        public class WebServer
        {
            public static void monoLocalWS()
            {
                HttpServer httpServer;
                httpServer = new MyHttpServer(8080);
                Thread thread = new Thread(new ThreadStart(httpServer.listen));
                thread.Start();
            }
        }
    }
}
