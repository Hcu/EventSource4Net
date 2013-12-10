using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Threading.Tasks;

namespace EventSource4Net
{
    class ConnectedState : IConnectionState
    {
        private static readonly slf4net.ILogger _logger = slf4net.LoggerFactory.GetLogger(typeof(ConnectedState));

        private HttpWebResponse mResponse;
        private IList<string> messageBuffer; //This buffer will contain all strings that when processed will lead to one ServerSentEvent

        public EventSourceState State { get { return EventSourceState.OPEN; } }

        public ConnectedState(HttpWebResponse resp)
        {
            mResponse = resp;
            messageBuffer = new List<string>();
        }

        public Task<IConnectionState> Run(Action<ServerSentEvent> msgReceived)
        {
            byte[] buffer = new byte[1024*8];
            Stream stream = mResponse.GetResponseStream();
            

            var taskRead = Task<int>.Factory.FromAsync<byte[], int, int>(stream.BeginRead,
                                                                         stream.EndRead, buffer, 0, buffer.Length,
                                                                         null);
            return taskRead.ContinueWith<IConnectionState>(tr =>
            {
                if (tr.Status == TaskStatus.RanToCompletion)
                {
                    int bytesRead = tr.Result;

                    if (bytesRead > 0) // stream has not reached the end yet
                    {
                        string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        text = text.Replace("\r\n", "\n\n"); //Some servers can send \r\n, so make sure that we convert those

                        while (text.Trim() != string.Empty )
                        {
                            var eventDelimiter = text.IndexOf("\n\n");
                            if (eventDelimiter != -1)
                            {
                                //We have the end of a ServerSentEvent, put everything in the buffer and process the event
                                messageBuffer.Add(text.Substring(0, eventDelimiter));
                                ProcessEvent(msgReceived);
                                //Be sure to continue as the server might have sent more data already
                                text = text.Substring(eventDelimiter+2);
                            }
                            else
                            {
                                //There was no end of ServerSentEvent detected, so add this string to the message buffer
                                messageBuffer.Add(text);
                                //We can break out of the loop, the messagebuffer will be dealt with when the server sents \n\n
                                break;
                            }
                        }
                        //Check for edge case where the server sent us the eventDelimiter
                        if (text == "\n\n")
                            ProcessEvent(msgReceived);

                        return this;
                    }
                    else // end of the stream reached
                    {
                        _logger.Trace("No bytes read. End of stream.");
                    }
                }
                return new DisconnectedState(mResponse.ResponseUri);
            });
        }

        private void ProcessEvent(Action<ServerSentEvent> msgReceived)
        {
            ServerSentEvent sse = new ServerSentEvent();
            foreach (string evt in messageBuffer)
            {
                string[] lines = evt.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.StartsWith(":"))
                    {
                        // This a comment, just log it.
                        _logger.Trace("A comment was received: " + line);
                    }
                    else
                    {
                        string fieldName = String.Empty;
                        string fieldValue = String.Empty;
                        if (line.Contains(':'))
                        {
                            int index = line.IndexOf(':');
                            fieldName = line.Substring(0, index);
                            fieldValue = line.Substring(index + 1).TrimStart();
                        }
                        else
                            fieldName = line;

                        if (String.Compare(fieldName, "event", true) == 0)
                        {
                            sse = sse ?? new ServerSentEvent();
                            sse.EventType = fieldValue;
                        }
                        else if (String.Compare(fieldName, "data", true) == 0)
                        {
                            sse = sse ?? new ServerSentEvent();
                            sse.Data = fieldValue + '\n';
                        }
                        else if (String.Compare(fieldName, "id", true) == 0)
                        {
                            sse = sse ?? new ServerSentEvent();
                            sse.LastEventId = fieldValue;
                        }
                        else if (String.Compare(fieldName, "retry", true) == 0)
                        {
                            int parsedRetry;
                            if (int.TryParse(fieldValue, out parsedRetry))
                            {
                                sse = sse ?? new ServerSentEvent();
                                sse.Retry = parsedRetry;
                            }
                        }
                        else
                        {
                            // Ignore this, just log it
                            _logger.Warn("A unknown line was received: " + line);
                        }
                    }
                }
            }
            _logger.Trace("Message received");
            msgReceived(sse);
            messageBuffer.Clear();
                    
        }


    }
}
