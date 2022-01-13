using Kucoin.Net;
using Kucoin.Net.Objects;

namespace kucoin_rebalancer
{
    class Program
    {
        static void Main() { MainAsync().GetAwaiter().GetResult(); }
        static async Task MainAsync()
        {
            List<PairInfo> pairs = new List<PairInfo>() {
                new PairInfo("LOVE-USDT", .5m),
                new PairInfo("TIDAL-USDT", .5m),
                };

            Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 10, Threshold: 0.03m, Paper: false);
            await r.Start();

            //Console.ReadKey blocks main thread
            await Task.Factory.StartNew(() => {
                while ((r.KeyPress = Console.ReadKey().Key) != ConsoleKey.Escape) ;
            });

            await r.Stop();
        }
    }

    class PairInfo
    {
        public string Pair;
        public decimal Percentage, ActualPercentage, Quantity = 0, Ask = 0;
        public PairInfo(string Pair, decimal Percentage)
        {
            this.Pair = Pair;
            this.Percentage = this.ActualPercentage = Percentage;
        }
    }

    class Rebalancer
    {
        public ConsoleKey KeyPress;
        public List<PairInfo> Pairs;
        decimal Amount, Threshold;
        bool HasQuantities = false, Paper = true;
        const string key = "xxx", secret = "xxx", pass = "xxx";
        KucoinSocketClient sc;
        KucoinClient kc;
        Dictionary<string, decimal> BaseMinSize = new Dictionary<string, decimal>(), BaseIncrement = new Dictionary<string, decimal>();

        public Rebalancer(List<PairInfo> Pairs, decimal Amount, decimal Threshold, bool Paper = true)
        {
            this.Paper = Paper;
            this.Pairs = Pairs;
            this.Amount = Amount;
            this.Threshold = Threshold;
            sc = new KucoinSocketClient(new KucoinSocketClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass), AutoReconnect = true, });
            kc = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass) });
        }

        public async Task Buy(PairInfo p, decimal Quantity)
        {
            decimal q = Round(Quantity, p.Pair);
            if (!Paper)
            {
                //q = BaseMinSize[p.Pair];
                var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Buy, type: KucoinNewOrderType.Market, quantity: q, clientOrderId: Guid.NewGuid().ToString());
                if (!res.Success) Console.WriteLine($"Buy error: {res.Error}");
            }
            p.Quantity += q;
            Console.WriteLine($"Bought {q} of {p.Pair} (${decimal.Round(q * p.Ask, 4)}), holding ${decimal.Round(p.Quantity * p.Ask, 4)}");
        }

        public async Task Sell(PairInfo p, decimal Quantity)
        {
            decimal q = Round(Quantity, p.Pair);
            if (!Paper)
            {
                //q = BaseMinSize[p.Pair];
                var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Sell, type: KucoinNewOrderType.Market, quantity: q, clientOrderId: Guid.NewGuid().ToString());
                if (!res.Success) Console.WriteLine($"Sell error: {res.Error}");
            }
            p.Quantity -= q;
            Console.WriteLine($"Sold {q} of {p.Pair} (${decimal.Round(q * p.Ask, 4)}), holding ${decimal.Round(p.Quantity * p.Ask, 4)}");
        }

        public decimal Round(decimal d, string pair)
        {
            int count = BitConverter.GetBytes(decimal.GetBits(BaseIncrement[pair])[3])[2];
            decimal min = BaseMinSize[pair];
            if (d < min)
            {
                Console.WriteLine($"Quantity {d} too small for {pair}--using BaseMinSize of {min}");
                return min;
            }
            else return decimal.Round(d, count);
        }

        public async Task Stop()
        {
            Console.WriteLine();
            foreach (PairInfo p in Pairs) await Sell(p, p.Quantity);
        }

        public async Task Start()
        {
            await UpdateBaseDictionaries();
            foreach (PairInfo Pair in Pairs)
            {
                var res = await sc.Spot.SubscribeToTickerUpdatesAsync(Pair.Pair, async data =>
                {
                    Pair.Ask = (decimal)data.Data.BestAsk;// * (1 + MinThreshold); //fees make it a bit higher?
                    if (Pair.Quantity == 0) Buy(Pair, (Pair.Percentage * Amount) / Pair.Ask).GetAwaiter().GetResult();
                    else
                    {
                        //ensure we are holding all pairs first--maybe a better way to check this
                        if (!HasQuantities)
                        {
                            HasQuantities = true;
                            foreach (PairInfo pi in Pairs) if (pi.Quantity == 0) HasQuantities = false;
                        }
                        else
                        {
                            Rebalance().GetAwaiter().GetResult();
                        }
                    }
                });
                if (!res.Success) Console.WriteLine(res.Error);
            }
        }

        void UpdateActualPercentage(PairInfo pi)
        {
            decimal SumUSDT = 0;
            foreach (PairInfo p in Pairs) SumUSDT += p.Ask * p.Quantity;
            pi.ActualPercentage = (pi.Ask * pi.Quantity) / SumUSDT;
        }

        async Task Rebalance()
        {
            //if + or - is pressed increase or decrease position size by 10%
            if (KeyPress == ConsoleKey.OemPlus) { KeyPress = 0; foreach (PairInfo pi in Pairs) await Buy(pi, pi.Quantity * 0.1m); }
            else if (KeyPress == ConsoleKey.OemMinus) { KeyPress = 0; foreach (PairInfo pi in Pairs) await Sell(pi, pi.Quantity * 0.1m); }

            int l = 0, t = 0;
            foreach (PairInfo pi in Pairs)
            {
                UpdateActualPercentage(pi);

                
                if (pi == Pairs.First()) (l, t) = Console.GetCursorPosition();
                Console.Write(Decimal.Round(100 * pi.ActualPercentage, 6) + $"% {pi.Pair} ");
                if (pi == Pairs.Last()) Console.SetCursorPosition(l, t);

                if (pi.ActualPercentage >= pi.Percentage + Threshold)
                {
                    Console.WriteLine($"\n{pi.Pair} crossed {100 * Threshold}% threshold!");
                    Sell(pi, pi.Quantity - pi.Quantity * (pi.Percentage / pi.ActualPercentage)).GetAwaiter().GetResult();
                    //UpdateActualPercentage(pi);

                    //buy/sell the other pair(s) 
                    foreach (PairInfo pi2 in Pairs.OrderByDescending(i => i.ActualPercentage))
                    {
                        if (pi2.Pair == pi.Pair) continue;
                        decimal q = pi2.Quantity - pi2.Quantity * (pi2.Percentage / pi2.ActualPercentage);
                        //Console.WriteLine($"Quantity: {q}, Percentage: {pi2.ActualPercentage}%");
                        if (q > BaseMinSize[pi2.Pair]) Sell(pi2, q).GetAwaiter().GetResult();
                        else if (-q > BaseMinSize[pi2.Pair]) Buy(pi2, -q).GetAwaiter().GetResult();
                        //UpdateActualPercentage(pi2);
                    }
                }
            }
        }

        async Task UpdateBaseDictionaries() 
        {
            HashSet<string> pairs = new HashSet<string>();
            foreach (PairInfo p in Pairs) pairs.Add(p.Pair);
            var sa = await kc.Spot.GetSymbolsAsync(); //"USDS"
            foreach (var pair in sa.Data)
            {
                if (pairs.Contains(pair.Name))
                {
                    BaseMinSize.Add(pair.Name, pair.BaseMinSize);
                    BaseIncrement.Add(pair.Name, pair.BaseIncrement);
                }
            }
        }
    }
}
