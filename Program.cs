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
                new PairInfo("LINK3S-USDT", .25m),
                //new PairInfo("ELON-USDT", .25m),
                new PairInfo("NEAR3L-USDT", .375m),
                new PairInfo("SAND3L-USDT", .375m),
                };

            //$5 initial investment, 0.2% threshold for rebalancing 
            Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 2000, Threshold: 0.002m, Paper: true);
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
        decimal Amount, Threshold, MinThreshold = 0.001m;
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
            var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Buy, type: KucoinNewOrderType.Market, quantity: Quantity, clientOrderId: Guid.NewGuid().ToString());
            if (!res.Success) Console.WriteLine($"Buy error: {res.Error}");
        }

        public async Task Sell(PairInfo p, decimal Quantity)
        {
            var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Sell, type: KucoinNewOrderType.Market, quantity: Quantity, clientOrderId: Guid.NewGuid().ToString());
            if (!res.Success) Console.WriteLine($"Sell error: {res.Error}");
        }

        public async Task BuyPercent(PairInfo Pair, decimal Percent)
        {
            decimal q = Round(Percent * Pair.Percentage * (Amount / Pair.Ask), Pair.Pair);
            Pair.Quantity += q;
            if (!Paper) await Buy(Pair, q);
            Console.WriteLine($"Bought {q} of {Pair.Pair} ({decimal.Round(100 * Pair.ActualPercentage, 4)}%, ${decimal.Round(q * Pair.Ask, 4)})");
        }

        public async Task SellPercent(PairInfo Pair, decimal Percent)
        {
            decimal q = Round(Percent * Pair.Percentage * (Amount / Pair.Ask), Pair.Pair);
            Pair.Quantity -= q;
            if (!Paper) await Sell(Pair, q);
            Console.WriteLine($"Sold {q} of {Pair.Pair} ({100 * Pair.ActualPercentage}%, ${q * Pair.Ask})");
        }

        public decimal Round(decimal d, string pair)
        {
            int count = BitConverter.GetBytes(decimal.GetBits(BaseIncrement[pair])[3])[2];
            decimal min = BaseMinSize[pair];
            if (d < min)
            {
                Console.WriteLine($"Quantity {d} to small for {pair}--using BaseMinSize of {min}");
                return min;
            }
            else return decimal.Round(d, count);
        }

        public async Task Stop()
        {
            Console.WriteLine();
            foreach (PairInfo p in Pairs) await SellPercent(p, 1);
        }

        public async Task Start()
        {
            await UpdateBaseDictionaries();
            foreach (PairInfo Pair in Pairs)
            {
                var res = await sc.Spot.SubscribeToTickerUpdatesAsync(Pair.Pair, async data =>
                {
                    Pair.Ask = (decimal)data.Data.BestAsk;
                    if (Pair.Quantity == 0) await BuyPercent(Pair, 1);
                    else
                    {
                        //ensure we are holding all pairs first--maybe a better way to check this
                        if (!HasQuantities)
                        {
                            HasQuantities = true;
                            foreach (PairInfo pi in Pairs) if (pi.Quantity == 0) HasQuantities = false;
                        }
                        else await Rebalance();
                    }
                });
                if (!res.Success) Console.WriteLine(res.Error);
            }
        }

        async Task Rebalance()
        {
            //if + or - is pressed increase or decrease position size by 2%
            if (KeyPress == ConsoleKey.OemPlus) { KeyPress = 0; foreach(PairInfo pi in Pairs) await BuyPercent(pi, .02m); }
            else if (KeyPress == ConsoleKey.OemMinus) { KeyPress = 0; foreach (PairInfo pi in Pairs) await SellPercent(pi, .02m); }

            //we need to calculate new percentages and check if any are above Threshold
            //if so, we need to sell that pair and buy other pair(s)
            decimal SumUSDT = 0;
            foreach (PairInfo pi in Pairs) SumUSDT += pi.Ask * pi.Quantity;
            foreach (PairInfo pi in Pairs)
            {
                pi.ActualPercentage = (pi.Ask * pi.Quantity) / SumUSDT;
                if (pi.ActualPercentage >= pi.Percentage + Threshold)
                {
                    Console.WriteLine($"\n{pi.Pair} crossed {100 * Threshold}% threshold!");

                    //convert amount over threshold as quantity to sell
                    decimal SellPercentage = pi.ActualPercentage - pi.Percentage, SmallBuyPecentage = 0;
                    await SellPercent(pi, SellPercentage);

                    //buy the other pair(s) 
                    foreach (PairInfo pi2 in Pairs.OrderByDescending(i => i.ActualPercentage))
                    {
                        if (pi2.Pair == pi.Pair) continue;
                        decimal BuyPercentage = pi2.Percentage - pi2.ActualPercentage;

                        //this would be without the additional logic
                        //if (BuyPercentage > MinThreshold) await BuyPercent(pi2, BuyPercentage);

                        if (BuyPercentage > 0) //only buy those pairs that dropped below Pecentage
                        {
                            if ((BuyPercentage + SmallBuyPecentage) <= MinThreshold)
                            {
                                Console.WriteLine($"Buy percentage too small: {100*(decimal.Round(BuyPercentage + SmallBuyPecentage,4))}% < {100*MinThreshold}% -- adding to following pair");
                                SmallBuyPecentage += BuyPercentage;
                                continue;
                            }
                            else
                            {
                                await BuyPercent(pi2, BuyPercentage + SmallBuyPecentage);
                                SmallBuyPecentage = 0; //reset this
                            }
                        }

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
