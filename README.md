# kucoin-rebalancer
Simple threshold coin rebalancer for Kucoin

Instructions:

Create new C# console application

Include Kucoin.NET from Project->Nuget

Update Kucoin API key, secret, pass in Rebalancer class

Define pairs, percentage amounts, usdt amount and threshold for rebalance:

Pass Paper=false in Rebalancer constructor to try it with real money

Press Esc to exit and sell to usdt, press plus key to add 10% to rebalancer and minus key to remove 10%

Added DCA option that allows for adding funds to all pairs when the average ask price deviates from the initial average ask price by set percentages

The values on the right represent the percentage allocations for each pair and the number of streaming client calls (I've also added dollar amount)

The number of streaming client calls is reset after each threshold cross or manual buy/sell

Uses market orders and it may have bugs - I do not guarantee it in any way

```csharp
List<PairInfo> pairs = new List<PairInfo>() {
                new PairInfo("LOVE-USDT", .5m),
                new PairInfo("SHIB-USDT", .5m),
                };

Rebalancer r = new Rebalancer(Pairs: pairs, Amount: 20, Threshold: .01m, Paper: true, DCA: ".05@-.5; .1@-1; .15@-5");

await r.Start();
```

![Screenshot](screenshot.png)

More info about coin rebalancing:

https://blog.shrimpy.io/blog/a-comparison-of-rebalancing-strategies-for-cryptocurrency-portfolios
