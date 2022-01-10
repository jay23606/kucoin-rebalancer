//using System; //not needed on 2022 apparently
//using System.Linq;
//using System.Collections.Generic;
//using System.Threading.Tasks;
//using CryptoExchange.Net.Objects;
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
                new PairInfo("BTC3L-USDT", .25m),
                new PairInfo("ETH3L-USDT", .25m),
                new PairInfo("FTM3S-USDT", .25m),
                new PairInfo("LINK3S-USDT", .25m),

                };

            //$100 initial investment, 0.3% threshold for rebalancing 
            //A larger threshold is probably better but this is just a test
            Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 5, Threshold: 0.005m, Paper: false); 
            await r.Start();

            Console.ReadLine();

            await r.Stop();

            Console.ReadLine();
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
        public List<PairInfo> Pairs;
        decimal Amount, Threshold, MinThreshold = 0.001m; //kucoin fees are 0.1% 
        bool HasQuantities = false, Paper = true;
        const string key = "xxx", secret = "xxx", pass = "xxx";
        KucoinSocketClient sc;
        KucoinClient kc;
        public Rebalancer(List<PairInfo> Pairs, decimal Amount, decimal Threshold, bool Paper=true)
        {
            this.Pairs = Pairs;
            this.Amount = Amount;
            this.Threshold = Threshold;
            sc = new KucoinSocketClient(new KucoinSocketClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass), AutoReconnect = true, });
            if (!Paper) kc = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass) });
        }
        

        //untested
        public async Task Buy(PairInfo p, decimal Quantity)
        {
            var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Buy, type: KucoinNewOrderType.Market, quantity: Quantity, clientOrderId: Guid.NewGuid().ToString());
            if (!res.Success) Console.WriteLine($"Buy error: {res.Error}");
        }

        //untested
        public async Task Sell(PairInfo p, decimal Quantity)
        {
            var res = await kc.Spot.PlaceOrderAsync(symbol: p.Pair, side: KucoinOrderSide.Sell, type: KucoinNewOrderType.Market, quantity: Quantity, clientOrderId: Guid.NewGuid().ToString());
            if (!res.Success) Console.WriteLine($"Sell error: {res.Error}");
            //else
            //{
            //    //var res2 = await kc.Spot.GetOrderByClientOrderIdAsync(res.Data.OrderId);
            //    //res2.Data.Price
            //    //may need to look at price info since it may not reflect the BestAsk from streaming client..
            //}
        }

        public async Task Stop()
        {
            if (kc == null && !Paper) kc = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(key, secret, pass) });
            foreach (PairInfo p in Pairs)
            {
                if (!Paper) await Sell(p, p.Quantity); 
                Console.WriteLine($"Sold {p.Quantity} of {p.Pair} ({100 * p.ActualPercentage}%, ${p.Quantity * p.Ask})");
            }
        }

        public async Task Start()
        {
            foreach (PairInfo Pair in Pairs)
            {
                var res = await sc.Spot.SubscribeToTickerUpdatesAsync(Pair.Pair, data =>
                {
                    Pair.Ask = (decimal)data.Data.BestAsk;

                    if(Pair.Quantity == 0)
                    {
                        //execute buy market order here and update Quantity with real figure
                        Pair.Quantity = decimal.Round(Pair.Percentage * (Amount / Pair.Ask), 8); //needs to be rounded?
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
                                    decimal SellQuantity = decimal.Round(SellPercentage * pi.Quantity, 8); //needs to be rounded?

                                    //execute sell market order here and update Quantity with real figure
                                    pi.Quantity = pi.Quantity - SellQuantity;
                                    if (!Paper) Sell(pi, SellQuantity).GetAwaiter().GetResult(); //not sure why await isn't possible
                                    Console.WriteLine($"Sold {SellQuantity} of {pi.Pair} ({100 * pi.ActualPercentage}%, ${SellQuantity * pi.Ask})");

                                    //buy the other pair(s) and make sure we buy SellPercentage amount
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
                                                    decimal BuyQuantity = decimal.Round((BuyPercentage + SmallBuyPecentage) * pi2.Quantity, 8); //needs to be rounded?

                                                    //execute buy market order here and update Quantity with real figure
                                                    pi2.Quantity = pi2.Quantity + BuyQuantity;
                                                    if (!Paper) Buy(pi2, pi2.Quantity).GetAwaiter().GetResult();
                                                    Console.WriteLine($"Bought {BuyQuantity} of {pi2.Pair} ({100 * pi2.ActualPercentage}%, ${BuyQuantity * pi2.Ask})");
                                                    
                                                    BoughtPercentage += BuyPercentage + SmallBuyPecentage;
                                                    SmallBuyPecentage = 0; //reset this
                                                }
                                            }
                                        }
                                    }

                                    //probably should make sure these are equal mostly
                                    Console.WriteLine($"SoldPercentage:BoughtPercentage -> {SellPercentage}:{BoughtPercentage}");

                                    //check the actual percentages for the pairs
                                    decimal SumPercent = 0, SumUSDT2 = 0;
                                    foreach (PairInfo pi2 in Pairs)
                                    {
                                        Console.WriteLine($"{pi2.Pair}: {100 * pi2.ActualPercentage}%");
                                        SumPercent += 100 * pi2.ActualPercentage;
                                        SumUSDT2 += pi2.Ask * pi2.Quantity;
                                    }
                                    Console.WriteLine($"Totals: ${SumUSDT2} ({SumPercent}%)");
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
