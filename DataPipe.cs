using System.IO.Pipes;
using System.Text;


namespace EthRandomCheck
{
    class DataPipe
    {
        private NamedPipeServerStream pipeServer = null;
        private StreamWriter pipeWriter = null;
        public string pipeName;
        public DataPipe(string name)
        {
            pipeName = name;
        }
        
        public async Task Start()
        {
            pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
 
            await pipeServer.WaitForConnectionAsync();

            pipeWriter = new StreamWriter(pipeServer, Encoding.UTF8) { AutoFlush = true };
        }
        public void Write(string message)
        {
            if (pipeWriter == null)
            {
              
                return;
            }
            pipeWriter.WriteLine(message);
            pipeWriter.Flush();
        }
        public void Stop()
        {
            pipeWriter?.Close();
            pipeServer?.Close();
            pipeServer?.Dispose();
        }
    }
}
