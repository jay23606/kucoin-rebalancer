# kucoin-rebalancer
Simple threshold coin rebalancer for Kucoin

Instructions:

Create new C# console application

Include Kucoin.NET from Project->Nuget

Update Kucoin API key, secret, pass in Rebalancer class

Set Paper=false in Rebalancer constructor to try it with real money

Define pairs, percentage amounts, usdt amount and threshold for rebalance:

Press Esc to exit and sell to usdt, press plus key to add 10% to rebalancer and minus key to remove 10%

The values on the right represent the percentage allocations for each pair and the number of streaming client calls

```csharp
List<PairInfo> pairs = new List<PairInfo>() {
    new PairInfo("BTC3L-USDT", .25m),
    new PairInfo("ETH3L-USDT", .25m),
    new PairInfo("FTM3S-USDT", .25m),
    new PairInfo("LINK3S-USDT", .25m),
    };

//$5 initial investment, 0.2% threshold for rebalancing 
Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 5, Threshold: 0.002m); 
await r.Start();
```

![Screenshot](screenshot.png)

More info about coin rebalancing:

https://blog.shrimpy.io/blog/a-comparison-of-rebalancing-strategies-for-cryptocurrency-portfolios
