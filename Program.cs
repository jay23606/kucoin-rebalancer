//Compiled with visual studio 2022 community
//Make sure to add Kucoin.Net from Project->Manage NuGet->Browse
using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kucoin.Net;
using Kucoin.Net.Objects;
using System.Runtime.InteropServices;

namespace kucoin_rebalancer
{
    class Program
    {
        static async Task Main()
        {
            List<PairInfo> pairs = new List<PairInfo>() {
                new PairInfo("LOVE-USDT", .5m),
                new PairInfo("SHIB-USDT", .5m),
                };

            Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 20, Threshold: .01m, Paper: true, DCA: ".05@-.5; .1@-1; .15@-5");

            await r.Start();

            //Console.ReadKey blocks main thread
            var timer = new System.Timers.Timer { Interval = 1000 * 60 * 5 };
            //var timer = new System.Timers.Timer { Interval = 1000 * 10 };
            timer.Elapsed += (sender, e) => PrintAvgPerformance(null, e, r);
            timer.Start();

            await Task.Run(() => 
            {
                do
                {
                    if (Console.KeyAvailable) r.KeyPress = Console.ReadKey().Key;
                } while (r.KeyPress != ConsoleKey.Escape);
            });

            await r.Stop();
        }
        static void PrintAvgPerformance(object sender, System.Timers.ElapsedEventArgs e, Rebalancer r)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Console.WriteLine();
            else foreach (PairInfo p in r.Pairs) Console.WriteLine();
            Console.WriteLine($"Average Performance: {decimal.Round(r.AvgPerformace,4)}%");
        }
    }

    class PairInfo
    {
        public string Pair;
        public decimal Percentage, ActualPercentage, Quantity = 0, Ask = 0, InitialAsk = 0;
        public int calls = 0, left = 0, top = 0;

        //how is the pair performing since we first purchased?
        public decimal Performance
        {
            get
            {
                if (InitialAsk == 0) return 0;
                return 100 * ((Ask / InitialAsk) - 1) - .2m;
            }
        }
        public PairInfo(string Pair, decimal Percentage)
        {
            this.Pair = Pair;
            this.Percentage = this.ActualPercentage = Percentage;
            this.left = 80;
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
        Stack<decimal> Scales = new Stack<decimal>();
        Stack<decimal> Deviations = new Stack<decimal>();
        
        public Rebalancer(List<PairInfo> Pairs, decimal Amount, decimal Threshold, bool Paper = true, string DCA = "")
        {
            if (DCA != "")
            {
                string[] sds = DCA.Split(";");
                foreach(string sd in sds.Reverse())
                {
                    string[] s = sd.Trim().Split("@");
                    if (decimal.TryParse(s[0].Trim(), NumberStyles.Currency, CultureInfo.CreateSpecificCulture("en-US"), out var scale))
                         Scales.Push(scale);
                    if (decimal.TryParse(s[1].Trim(), NumberStyles.Currency, CultureInfo.CreateSpecificCulture("en-US"), out var deviation))
                        Deviations.Push(deviation);
                }
            }

            for (int i = 0; i < Pairs.Count; i++) Pairs[i].top = i;
            this.Paper = Paper;
            this.Pairs = Pairs;
            this.Amount = Amount;
            this.Threshold = Threshold;
            sc = new KucoinSocketClient(new KucoinSocketClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass), AutoReconnect = true, });
            kc = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass) });
        }

        public decimal AvgPerformace
        {
            get
            {
                return Pairs.Sum(x => x.ActualPercentage * x.Performance) / Pairs.Sum(x => x.ActualPercentage);
            }
        }

        public async Task Buy(PairInfo p, decimal Quantity)
        {
            decimal q = Round(Quantity, p.Pair);
            if (!Paper && q > 0)
            {
                var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Buy, type: KucoinNewOrderType.Market, quantity: q, clientOrderId: Guid.NewGuid().ToString());
                if (!res.Success) Console.WriteLine($"Buy error: {res.Error}");
            }
            if (p.InitialAsk == 0) p.InitialAsk = p.Ask;
            p.Quantity += q;
            Console.WriteLine($"Bought {q} of {p.Pair} (${decimal.Round(q * p.Ask, 4)}), holding ${decimal.Round(.998m * p.Quantity * p.Ask, 4)}");
        }

        public async Task Sell(PairInfo p, decimal Quantity)
        {
            decimal q = Round(Quantity, p.Pair);
            if (!Paper && q > 0)
            {
                var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Sell, type: KucoinNewOrderType.Market, quantity: q, clientOrderId: Guid.NewGuid().ToString());
                if (!res.Success) Console.WriteLine($"Sell error: {res.Error}");
            }
            p.Quantity -= q;
            Console.WriteLine($"Sold {q} of {p.Pair} (${decimal.Round(q * p.Ask, 4)}), holding ${decimal.Round(.998m * p.Quantity * p.Ask, 4)}");
        }

        public decimal Round(decimal d, string pair)
        {
            int count = BitConverter.GetBytes(decimal.GetBits(BaseIncrement[pair])[3])[2];
            decimal min = BaseMinSize[pair];
            if (d < min)
            {
                Console.WriteLine($"Quantity {d} too small for {pair}--minimum is {min}");
                //return min;
                //rather than return the min I am going to play it safe and return zero
                return 0;
            }
            else return decimal.Round(d, count);
        }

        public async Task Stop()
        {
            Console.WriteLine();
            sc.Spot.UnsubscribeAllAsync().GetAwaiter().GetResult();
            foreach (PairInfo p in Pairs) Sell(p, p.Quantity).GetAwaiter().GetResult();
        }

        public async Task Start()
        {
            await UpdateBaseDictionaries();
            foreach (PairInfo Pair in Pairs)
            {
                var res = await sc.Spot.SubscribeToTickerUpdatesAsync(Pair.Pair, async data =>
                {
                    Pair.Ask = (decimal)data.Data.BestAsk;
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
                            Pair.calls++;
                            Rebalance(Pair).GetAwaiter().GetResult();
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

        async Task Rebalance(PairInfo Pair)
        {
            //if + or - is pressed increase or decrease position size by 10%
            if (KeyPress == ConsoleKey.OemPlus) { KeyPress = 0; Console.WriteLine();  foreach (PairInfo pi in Pairs) Buy(pi, pi.Quantity * 0.1m).GetAwaiter().GetResult(); }
            else if (KeyPress == ConsoleKey.OemMinus) { KeyPress = 0; Console.WriteLine(); foreach (PairInfo pi in Pairs) Sell(pi, pi.Quantity * 0.1m).GetAwaiter().GetResult(); }

            if (Deviations.Count > 0)
            {
                if (AvgPerformace < Deviations.Peek())
                {
                    Console.WriteLine($"\nAvgPerformance {decimal.Round(AvgPerformace, 2)}% < {decimal.Round(Deviations.Peek(), 2)}% - executing {decimal.Round(100 * Scales.Peek(), 2)}% DCA");
                    foreach (PairInfo pi in Pairs) Buy(pi, pi.Quantity * Scales.Peek()).GetAwaiter().GetResult();
                    Deviations.Pop();
                    Scales.Pop();
                }
            }
            
            foreach (PairInfo pi in Pairs)
            {
                UpdateActualPercentage(pi);

                int left = 0, top = 0;
                (left, top) = Console.GetCursorPosition();
                Console.SetCursorPosition(pi.left, top + pi.top);
                Console.Write(decimal.Round(100 * pi.ActualPercentage, 2) + $"% {pi.Pair} ${decimal.Round(.998m * pi.Ask * pi.Quantity, 2)} {decimal.Round(pi.Performance, 2)}% ({pi.calls})");
                Console.SetCursorPosition(left, top);


                //if (pi.ActualPercentage >= pi.Percentage + Threshold)
                // we need to do the percentage of the part not of the whole..
                if ((pi.ActualPercentage - pi.Percentage) / pi.Percentage >= Threshold)
                {

                    Console.WriteLine($"\n{pi.Pair} crossed {100 * Threshold}% threshold!");
                    Sell(pi, pi.Quantity - pi.Quantity * (pi.Percentage / pi.ActualPercentage)).GetAwaiter().GetResult();

                    //buy/sell the other pair(s) 
                    foreach (PairInfo pi2 in Pairs.OrderByDescending(i => i.ActualPercentage))
                    {
                        pi2.calls = 0;

                        if (pi2.Pair == pi.Pair || pi2.ActualPercentage == 0) continue;
                        decimal q = pi2.Quantity - pi2.Quantity * (pi2.Percentage / pi2.ActualPercentage);
                        //Console.WriteLine($"Quantity: {q}, Percentage: {pi2.ActualPercentage}%");
                        if (q > BaseMinSize[pi2.Pair]) Sell(pi2, q).GetAwaiter().GetResult();
                        else if (-q > BaseMinSize[pi2.Pair]) Buy(pi2, -q).GetAwaiter().GetResult();
                        else
                        {
                            //may want to find a way to distribute the difference here?
                            if (q > 0) Console.WriteLine($"${decimal.Round(q * pi2.Ask, 4)} for {pi2.Pair} is too small to sell");
                            else Console.WriteLine($"${decimal.Round(-q * pi2.Ask, 4)} for {pi2.Pair} is too small to buy");
                        }
                    }
                }
            }
        }

        async Task UpdateBaseDictionaries() 
        {
            HashSet<string> pairs = new HashSet<string>();
            foreach (PairInfo p in Pairs) pairs.Add(p.Pair);
            var sa = await kc.Spot.GetSymbolsAsync(); //"USDS" some 3L/3S are not in USDS market
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
