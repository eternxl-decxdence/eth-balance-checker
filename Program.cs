using EthRandomCheck;
using NBitcoin;
using Nethereum.HdWallet;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Numerics;
using System.Text;
using System.Text.Json;

class Program
{
    static bool isRunning = true;
    static string? adminUrl;
    static string? resultsFile;
    static string? rpcUrl;
    static int addrPerSeed = 1;
    static int maxThreads;
    static int localPort;
    static int batchSize = 1000;
    static ConcurrentBag<string> results = new ConcurrentBag<string>();
    static int generatedAddresses = 0;

    //fancy colors
    static string NORMAL = "\x1b[39m";
    static string RED = "\x1b[91m";
    static string YELLOW = "\x1b[93m";
    static string CYAN = "\x1b[96m";
    //fancy colors
    static async Task Main(string[] args)
    {
        #region Parse Arguments
        foreach (string arg in args)
        {
            if (arg.StartsWith("--adminUrl="))
            {
                adminUrl = arg.Substring("--adminUrl=".Length);
            }
            else if (arg.StartsWith("--resultsFile="))
            {
                resultsFile = arg.Substring("--resultsFile=".Length);
            }
            else if (arg.StartsWith("--rpcUrl="))
            {
                rpcUrl = arg.Substring("--rpcUrl=".Length);
            }
            else if (arg.StartsWith("--addrPerSeed="))
            {
                Int32.TryParse(arg.Substring("--addrPerSeed=".Length), out addrPerSeed);
            }
            else if (arg.StartsWith("--maxThreads="))
            {
                Int32.TryParse(arg.Substring("--maxThreads=".Length), out maxThreads);
            }
            else if (arg.StartsWith("--localPort="))
            {
                Int32.TryParse(arg.Substring("--localPort=".Length), out localPort);
            }
        }
        #endregion

        #region Request Missing Arguments
        if (string.IsNullOrEmpty(adminUrl))
        {
            Console.Write("Введите URL администратора: ");
            adminUrl = Console.ReadLine();
        }

        if (string.IsNullOrEmpty(resultsFile))
        {
            Console.Write("Введите путь для сохранения результатов: ");
            resultsFile = Console.ReadLine();
        }

        if (string.IsNullOrEmpty(rpcUrl))
        {
            Console.Write("Введите URL RPC API: ");
            rpcUrl = Console.ReadLine();
        }

        if (addrPerSeed <= 0)
        {
            Console.Write("Введите количество адресов на каждую сид-фразу: ");
            while (!Int32.TryParse(Console.ReadLine(), out addrPerSeed) || addrPerSeed <= 0)
            {
                Console.WriteLine("Введите положительное целое число.");
            }
        }

        if (maxThreads <= 0)
        {
            Console.WriteLine($"Количество логических ядер процессора {RED}{Environment.ProcessorCount}{NORMAL}. Рекомендуемо максимальное количество потоков генерации {RED}{Environment.ProcessorCount * 4}{NORMAL}");
            Console.Write("Введите количество потоков генерации: ");
            while (!Int32.TryParse(Console.ReadLine(), out maxThreads) || maxThreads <= 0)
            {
                Console.WriteLine("Введите положительное целое число.");
            }
        }
        if (localPort <= 0)
        {
            Console.Write($"Введите порт локального сервера (100-65535): ");
            while (!Int32.TryParse(Console.ReadLine(), out localPort) || localPort <= 0 || localPort > 65535 || localPort < 100)
            {
                Console.WriteLine("Введите положительное целое число от 100 до 65535.");
            }
        }
        #endregion


        await RunGenerator();

        Console.ReadLine();


    }

    static async Task RunGenerator()
    {
        int requestCount = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        DataPipe AuxDataPipe = new("AuxDataPipe");
        await AuxDataPipe.Start();

        _ = Task.Run(async () =>
        {
            while (true)
            {        
                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                object stats = new
                {
                    reqProcessed = $"{Interlocked.CompareExchange(ref requestCount, 0, 0)}",
                    addrGenerated = $"{Interlocked.CompareExchange(ref generatedAddresses, 0, 0)}",
                    avgRequests = $"{(elapsedSeconds == 0 ? 0 : Interlocked.CompareExchange(ref requestCount, 0, 0) / elapsedSeconds):F2}",
                    avgAddresses = $"{(elapsedSeconds == 0 ? 0 : Interlocked.CompareExchange(ref generatedAddresses, 0, 0) / elapsedSeconds):F2}"
                };

                string jsonData = JsonSerializer.Serialize(stats).Trim();
                await AuxDataPipe.Write(jsonData);
                await Task.Delay(1000); // Ожидание 1 секунда перед следующим вызовом
            } 
        });
       

        using (SemaphoreSlim semaphore = new SemaphoreSlim(maxThreads))
        {
            List<Task> tasks = new List<Task>();
            HttpClient httpClient = new HttpClient();
            

            while (isRunning)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        Dictionary<string, Wallet> AS_Dictionary = new Dictionary<string, Wallet>();
                        List<decimal> balances = new List<decimal>();

                        for (int i = batchSize / addrPerSeed; i > 0; i--)
                        {
                            Wallet wallet = new Wallet(Wordlist.English, WordCount.Twelve);
                            List<string> addresses = GenerateAddresses(wallet, addrPerSeed);

                            foreach (string address in addresses)
                            {
                                AS_Dictionary.Add(address, wallet);
                                Interlocked.Increment(ref generatedAddresses);
                            }
                        }
                        balances = await CheckBalanceBatchAsync(AS_Dictionary, httpClient);
                        Interlocked.Increment(ref requestCount);
                        
                        foreach ((int index, decimal balance) in balances.Select((item, index) => (index, item)))
                        {
                            Console.WriteLine($"{CYAN}[ {AS_Dictionary.ElementAt(index).Key} ]{NORMAL}{YELLOW}[ {balance} ETH ]{NORMAL}{RED}[ {string.Join(" ", AS_Dictionary.ElementAt(index).Value.Words)} ]{NORMAL}");
                            if (balance > 0)
                            {
                                byte[] privateKey = AS_Dictionary.ElementAt(index).Value.GetPrivateKey(0);
                                string strPrivateKey = Encoding.UTF8.GetString(privateKey, 0, privateKey.Length);
                                string result = JsonSerializer.Serialize(new { Address = AS_Dictionary.ElementAt(index).Key, Balance = balance, PrivateKey = strPrivateKey, Token = "ETH" });
                                results.Add(result);
                                // Отправляем данные администратору
                                // Results => api/send-results => api/throw-data-to-client => ReactComponent    
                            }
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                       
                    }
                    catch (JsonException ex)
                    {
                        
                    }
                    catch (Exception ex)
                    {
                  
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));

                tasks = tasks.Where(t => !t.IsCompleted).ToList();
            }
            await Task.WhenAll(tasks);
        }
    }
    static List<string> GenerateAddresses(Wallet wallet, int count)
    {
        try
        {
            List<string> addresses = new List<string>();

            for (int i = 0; i < count; i++)
            {
                string address = wallet.GetAccount(i).Address;
                addresses.Add(address);
                Interlocked.Increment(ref generatedAddresses);
            }
            return addresses;
        }
        catch (Exception ex)
        {
            return null;
        }
        
    }
    static async Task<List<decimal>> CheckBalanceBatchAsync(Dictionary<string, Wallet> AS_Pairs, HttpClient httpClient) 
    {
        List<decimal> balances = new List<decimal>(new decimal[AS_Pairs.Count]);
        Dictionary<int, int> idToIndex = new Dictionary<int, int>();

        int requestId = 0;
        List<object> batchRequest = new List<object>();

        try
        {
            foreach(string address in AS_Pairs.Keys) { 
                idToIndex[requestId] = requestId; // Привязываем id к индексу
                batchRequest.Add(new
                {
                    jsonrpc = "2.0",
                    method = "eth_getBalance",
                    @params = new[] { address, "latest" },
                    id = requestId
                });
                requestId++;
            }

            string jsonRequest = JsonSerializer.Serialize(batchRequest);
            StringContent content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            // Отправка запроса
            HttpResponseMessage response = await httpClient.PostAsync(rpcUrl, content);
            string responseString = await response.Content.ReadAsStringAsync();
            JsonDocument jsonResponse = JsonDocument.Parse(responseString);

            // Разбираем batch-ответ
            foreach (JsonElement item in jsonResponse.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement idElement) &&
                    item.TryGetProperty("result", out JsonElement result))
                {
                    int rpcId = idElement.GetInt32(); // ID запроса

                    if (idToIndex.TryGetValue(rpcId, out int index)) // Проверяем, есть ли такой id
                    {
                        string balanceHex = result.GetString();
                        BigInteger balanceWei = BigInteger.Parse(balanceHex.Substring(2), System.Globalization.NumberStyles.HexNumber);
                        balances[index] = (decimal)balanceWei / (decimal)Math.Pow(10, 18);
                    }
                }
            }
            return balances;
        }
        catch (Exception ex) 
        {
            return balances;
        }
    }
}
