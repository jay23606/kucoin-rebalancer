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
                new PairInfo("SHIB-USDT", .25m),
                new PairInfo("ELON-USDT", .25m),
                new PairInfo("SOS-USDT", .25m),
                new PairInfo("SRK-USDT", .25m),
                };

            //$5 initial investment, 0.2% threshold for rebalancing 
            Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 5, Threshold: 0.004m, Paper: false);
            await r.Start();

            //Console.ReadKey blocks main thread
            await Task.Factory.StartNew(() => { while (Console.ReadKey().Key != ConsoleKey.Escape) ; });

            await r.Stop();
        }
    }

    class PairInfo
    {
        public string Pair;
        public decimal Percentage, ActualPercentage, Quantity = 0, Ask = 0, ProfitPercent = 0, ProfitAmount = 0;

        public Queue<PriceVolume> PriceVolume;
        public PairInfo(string Pair, decimal Percentage)
        {
            this.Pair = Pair;
            this.Percentage = this.ActualPercentage = Percentage;
            PriceVolume = new Queue<PriceVolume>();
        }

        //Must update FIFO queue after sells in order to be able to calculate PnL
        public void UpdatePriceVolume(decimal SellVolume)
        {
            PriceVolume front = PriceVolume.Peek();
            if (SellVolume >= front.Volume)
            {
                do
                {
                    SellVolume -= front.Volume;
                    PriceVolume.Dequeue();
                    if (SellVolume == 0) return;
                    front = PriceVolume.Peek();
                } while (SellVolume >= front.Volume);
                if(SellVolume>0) front.Volume -= SellVolume;
            }
            else front.Volume -= SellVolume;
        }
    }

    class PriceVolume
    {
        public decimal Price, Volume;
        public PriceVolume(decimal Price, decimal Volume)
        {
            this.Price = Price;
            this.Volume = Volume;
        }
    }

    class Rebalancer
    {
        public List<PairInfo> Pairs;
        decimal Amount, Threshold, MinThreshold = 0.001m, Profits = 0; //kucoin fees are 0.1% 
        bool HasQuantities = false, Paper = true;
        const string key = "xxx", secret = "xxx", pass = "xxx";
        KucoinSocketClient sc;
        KucoinClient kc;
        Dictionary<string, decimal> BaseMinSize = new Dictionary<string, decimal>();
        Dictionary<string, decimal> BaseIncrement = new Dictionary<string, decimal>();

        public Rebalancer(List<PairInfo> Pairs, decimal Amount, decimal Threshold, bool Paper = true)
        {
            this.Paper = Paper;
            this.Pairs = Pairs;
            this.Amount = Amount;
            this.Threshold = Threshold;
            sc = new KucoinSocketClient(new KucoinSocketClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass), AutoReconnect = true, });
            if (!Paper) kc = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass) });
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

        public async Task Stop()
        {
            if (kc == null && !Paper) kc = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass) });
            foreach (PairInfo p in Pairs)
            {
                if (!Paper) await Sell(p, p.Quantity); //.GetAwaiter().GetResult();
                Console.WriteLine($"Sold {p.Quantity} of {p.Pair} ({100 * p.ActualPercentage}%, ${p.Quantity * p.Ask})");
            }
        }

        //alternate implementation 
        public async Task Start2()
        {
            foreach (PairInfo Pair in Pairs)
            {
                var res = await sc.Spot.SubscribeToTickerUpdatesAsync(Pair.Pair, data =>
                {
                    Pair.Ask = (decimal)data.Data.BestAsk;
                });
            }
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

        public async Task Start()
        {
            if (!Paper)
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

            foreach (PairInfo Pair in Pairs)
            {
                var res = await sc.Spot.SubscribeToTickerUpdatesAsync(Pair.Pair, data =>
                {
                    Pair.Ask = (decimal)data.Data.BestAsk;

                    if(Pair.Quantity == 0)
                    {
                        //int count = BitConverter.GetBytes(decimal.GetBits(BaseIncrement[Pair.Pair])[3])[2];
                        Pair.Quantity = Round(Pair.Percentage * (Amount / Pair.Ask), Pair.Pair);
                        Pair.PriceVolume.Enqueue(new PriceVolume(Pair.Ask * (1 + MinThreshold), Pair.Quantity)); //include 0.1% fee
                        if (!Paper) Buy(Pair, Pair.Quantity).GetAwaiter().GetResult();
                        Console.WriteLine($"Bought {Pair.Quantity} of {Pair.Pair} ({100 * Pair.ActualPercentage}%, ${Pair.Quantity * Pair.Ask})");
                    }
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
                            //we need to calculate new percentages and check if any are above Threshold
                            //if so, we need to sell that pair and buy other pair(s)
                            decimal SumUSDT = 0;
                            foreach (PairInfo pi in Pairs) SumUSDT += pi.Ask * pi.Quantity;

                            foreach (PairInfo pi in Pairs)
                            {
                                
                                pi.ActualPercentage = (pi.Ask * pi.Quantity) / SumUSDT;
                                //Console.WriteLine($"{pi.ActualPercentage} > {pi.Percentage + Threshold}");
                                if (pi.ActualPercentage >= pi.Percentage + Threshold)
                                {
                                    Console.WriteLine($"{pi.Pair} crossed {100 * Threshold}% threshold!");

                                    //convert amount over threshold as quantity to sell
                                    decimal SellPercentage = pi.ActualPercentage - pi.Percentage;
                                    decimal SellQuantity = Round(SellPercentage * pi.Quantity, pi.Pair); //needs to be rounded?

                                    //execute sell market order 
                                    pi.Quantity = pi.Quantity - SellQuantity;
                                    //pi.PriceVolume.Dequeue();
                                    pi.UpdatePriceVolume(SellQuantity);
                                    if (!Paper) Sell(pi, SellQuantity).GetAwaiter().GetResult(); //not sure why await isn't possible
                                    Console.WriteLine($"Sold {SellQuantity} of {pi.Pair} ({100 * pi.ActualPercentage}%, ${SellQuantity * pi.Ask})");
                                    Profits += SellQuantity * pi.Ask * (1 - MinThreshold); //include 0.1% fee

                                    //buy the other pair(s) 
                                    decimal SmallBuyPecentage = 0, BoughtPercentage = 0;
                                    foreach (PairInfo pi2 in Pairs.OrderByDescending(i => i.ActualPercentage))
                                    {
                                        if (pi2.Pair != pi.Pair)
                                        {
                                            decimal BuyPercentage = pi2.Percentage - pi2.ActualPercentage;
                                            if (BuyPercentage > 0) //only buy those pairs that dropped below Pecentage
                                            {
                                                if ((BuyPercentage + SmallBuyPecentage) <= MinThreshold)
                                                {
                                                    Console.WriteLine($"Buy percentage too small: {BuyPercentage + SmallBuyPecentage} < {MinThreshold}--adding % to next pair");
                                                    //add it to the next pair because 0.1% is too small to buy
                                                    SmallBuyPecentage += BuyPercentage;
                                                    continue;
                                                }
                                                else
                                                {
                                                    decimal BuyQuantity = Round((BuyPercentage + SmallBuyPecentage) * pi2.Quantity, pi2.Pair); //needs to be rounded?

                                                    //execute buy market order  
                                                    pi2.Quantity = pi2.Quantity + BuyQuantity;
                                                    pi2.PriceVolume.Enqueue(new PriceVolume(pi2.Ask * (1 + MinThreshold), pi2.Quantity)); //include 0.1% fee
                                                    if (!Paper) Buy(pi2, pi2.Quantity).GetAwaiter().GetResult();
                                                    Console.WriteLine($"Bought {BuyQuantity} of {pi2.Pair} ({100 * pi2.ActualPercentage}%, ${BuyQuantity * pi2.Ask})");
                                                    
                                                    BoughtPercentage += BuyPercentage + SmallBuyPecentage;
                                                    SmallBuyPecentage = 0; //reset this
                                                }
                                            }
                                        }
                                    }

                                    //there may be some disparity
                                    Console.WriteLine($"SoldPercentage:BoughtPercentage -> {decimal.Round(SellPercentage, 4)}:{decimal.Round(BoughtPercentage, 4)}");

                                    //check the actual percentages for the pairs
                                    decimal SumPercent = 0, SumUSDT2 = 0, SumProfitAmount = 0;
                                    foreach (PairInfo pi2 in Pairs)
                                    {
                                        decimal SumVol = 0, Avg = 0;
                                        foreach (PriceVolume pv in pi2.PriceVolume) SumVol += pv.Volume;
                                        foreach (PriceVolume pv in pi2.PriceVolume) Avg += (pv.Price * pv.Volume) / SumVol;

                                        decimal ProfitLoss = 0;
                                        if (Avg > 0) ProfitLoss = ((pi2.Ask / Avg) - 1);
                           
                                        pi2.ProfitPercent = 100 * ProfitLoss;

                                        //I think this may be wrong
                                        //pi2.ProfitAmount = (SumVol * pi2.Ask) - (SumVol * Avg);
                                        pi2.ProfitAmount = ProfitLoss * (pi2.Quantity * pi2.Ask);

                                        SumProfitAmount += pi2.ProfitAmount;

                                        //I think it's right
                                        Console.WriteLine($"{pi2.Pair}: {decimal.Round(100 * pi2.ActualPercentage, 4)}% FIFO PnL: {decimal.Round(pi2.ProfitPercent, 4)}%, ${decimal.Round(pi2.ProfitAmount, 4)}");
                                        SumPercent += 100 * pi2.ActualPercentage;
                                        
                                        SumUSDT2 += pi2.Ask * pi2.Quantity;
                                    }
                                    Console.WriteLine($"Totals: ${decimal.Round(SumUSDT2, 4)} ({decimal.Round(SumPercent, 4)}%), Sell Profits: ${decimal.Round(Profits, 4)}, Hodl PnL: ${decimal.Round(SumProfitAmount, 4)} @ {DateTime.Now}");
                                }
                            }

                        }
                    }
                });

                if (!res.Success) Console.WriteLine(res.Error);
            }
        }
    }
}
