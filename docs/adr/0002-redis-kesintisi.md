# ADR 0002 — Redis kesintisinde davranış: kesici + seçilebilir mod

**Durum:** Kabul edildi

## Bağlam

Rate limiter bir bağımlılığa (Redis) bağlı. Redis yavaşlarsa/düşerse her istek zaman aşımını bekler; gateway'in kendisi
kesinti kaynağına dönüşür.

## Karar

- Redis çağrısına sert üst sınır: `CommandTimeoutMs` (varsayılan 50 ms).
- Hata olunca **kesici açılır** (`BreakerCooldownSeconds`, varsayılan 5 sn): bu süre boyunca Redis'e hiç gidilmez, karar yerelden verilir.
  Test: ölü store'a 20 istekte yalnızca 1 deneme yapılır.
- Mod seçilebilir (`OnFailure`):
  - `LocalFallback` (varsayılan): node bazında bellek içi limit. Koruma sürer, ama toplam limit `N × limit` olur.
  - `FailOpen`: her şeyi geçir (erişilebilirlik > koruma).
  - `FailClosed`: her şeyi reddet (koruma > erişilebilirlik).
- Degrade kararlar `netroute.ratelimit.fallbacks` metriğine ve `degraded` etiketine işlenir; uyarı yalnızca geçişte loglanır (log seli yok).

## Sonuçlar

Varsayılan mod yanlış değil, **bilinçli bir ödünleşim**; hangi modun doğru olduğu iş yüküne bağlıdır (ödeme API'si için `FailClosed`,
herkese açık içerik için `FailOpen` tercih edilebilir).
