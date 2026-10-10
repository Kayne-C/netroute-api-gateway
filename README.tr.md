# NetRoute — YARP API Gateway ve Dağıtık Rate Limiting Motoru

[English](README.md) · **Türkçe**

Mikroservislerin önüne konan, **.NET 10 + YARP** tabanlı bir API gateway. Tek bir noktada yönlendirme, JWT doğrulama,
Redis destekli **dinamik** rate limiting ve yük dengeleme yapar; rota ve limit politikaları **yeniden başlatmadan** değiştirilir.

## Neler yapıyor

| Alan | Yaklaşım | Kanıt |
|---|---|---|
| **Merkezi yönlendirme** | YARP; rota/cluster/politika tek bir durum deposunda (`StateStore`), özel `IProxyConfigProvider` ile canlı uygulanır | Test: admin API ile eklenen rota yeniden başlatmadan canlı, silinince 404 |
| **Dağıtık rate limiting** | Token bucket, **tek Lua betiği** ile Redis'te atomik; saat olarak Redis `TIME` kullanılır (node saat farkı etkisiz) | Test: iki gateway node'u, 4000 eşzamanlı istek, 200 token → **tam 200** kabul (aşım yok) |
| **Dinamik politika** | `PUT /admin/rate-limit-policies/{ad}` bir sonraki istekte geçerli; JWT `tier` claim'i ile kademeli limitler (free/premium) | Test: limit yükseltilince bloklanan istemci toparlanır |
| **Redis kesintisi** | Zaman aşımı 50 ms, kısa devre kesici, `LocalFallback` / `FailOpen` / `FailClosed` seçilebilir; istek **hata vermez**, düşer | Test: ulaşılamaz Redis'te limitler node bazında sürer; kesici ölü store'a 1 deneme yapar, süre dolunca geri döner |
| **JWT doğrulama** | Kenarda doğrulanır (imza, süre, issuer/audience); kimlik `X-User-*` başlıklarıyla backend'e aktarılır, istemcinin gönderdiği aynı adlı başlıklar **silinir** | Test: sahte/süresi dolmuş token 401, `X-User-Id: admin` taklidi etkisiz |
| **Varsayılan kapalı** | Açıkça `anonymous` işaretlenmeyen her rota token ister; yapılandırma yoksa gateway **başlamaz** | Test |
| **Yük dengeleme** | YARP: `RoundRobin`, `PowerOfTwoChoices`, `LeastRequests`...; aktif (`/healthz`) + pasif (taşıma hatası oranı) sağlık kontrolü | Test: 3 backend'e 90 istek → 30/30/30; ölen backend öğrenildikten sonra **0 hata** |
| **Kimlik doğrulaması öncesi sınır** | Rate limit, yetkilendirmeden önce çalışır; token'sız sel isteği IP başına kısılır | Test |
| **Gözlemlenebilirlik** | OpenTelemetry (ASP.NET Core + `Yarp.ReverseProxy` + `NetRoute` metrikleri: karar sayısı, fallback, karar süresi), OTLP ile dışa aktarım | — |

## Hızlı başlangıç

```bash
docker compose up -d --build            # gateway + 2 backend + Redis
TOKEN=$(curl -s "localhost:5100/dev/token?sub=ayse&tier=premium" | jq -r .token)
curl localhost:5100/api/echo/ -H "authorization: Bearer $TOKEN"    # echo-1 / echo-2 dönüşümlü
curl -i localhost:5100/public/                                      # anonim rota, RateLimit-* başlıkları

# limiti canlı değiştir
ADMIN=$(curl -s "localhost:5100/dev/token?sub=root&role=admin" | jq -r .token)
curl -X PUT localhost:5100/admin/rate-limit-policies/public -H "authorization: Bearer $ADMIN" \
     -H 'content-type: application/json' -d '{"capacity":5,"refillPerSecond":1}'
```

Docker'sız: `dotnet run --project src/NetRoute.Gateway` (Redis yoksa tek-node bellek içi limiter), iki backend için
`INSTANCE_ID=echo-1 ASPNETCORE_URLS=http://127.0.0.1:5201 dotnet run --project src/NetRoute.SampleService`.

## Admin API (`admin` rolü gerekir)

| | |
|---|---|
| `GET /admin/state` | Rotalar, cluster'lar, politikalar |
| `PUT/DELETE /admin/routes/{id}` | YARP'ın kendi doğrulayıcısından geçer; bilinmeyen cluster/politika → 400 |
| `PUT/DELETE /admin/clusters/{id}` | Bir rota tarafından kullanılan cluster silinemez (409) |
| `PUT/DELETE /admin/rate-limit-policies/{ad}` | Varsayılan politika silinemez (409) |

Rota bir politikayı `metadata.RateLimitPolicy` ile seçer (`none` = limitsiz). Örnek yapılandırma: [netroute.routes.json](src/NetRoute.Gateway/netroute.routes.json).

## Ölçüm

Aynı makinede (loopback), tek süreçte gateway + 2 backend + Redis 8 (Docker), 64 eşzamanlı kapalı döngü istemci, 10 sn, Release:

| Senaryo | rps | p50 | p95 | p99 |
|---|---|---|---|---|
| Doğrudan tek backend | 28.237 | 2,22 ms | 3,30 ms | 3,93 ms |
| Gateway → anonim rota (Redis limiter) | 35.222 | 1,74 ms | 2,62 ms | 3,81 ms |
| Gateway → JWT + Redis limiter, 2 backend | 35.220 | 1,75 ms | 2,56 ms | 3,22 ms |

**Bu tablo gateway'in "daha hızlı" olduğunu göstermez.** İstemci, gateway ve backend'ler aynı CPU'yu paylaşıyor; gateway yükü iki
backend'e yaydığı için doğrudan tek backend'den yüksek çıkıyor. Anlamlı çıkarım yalnızca şu: Redis'li limiter + JWT doğrulama açıkken
tek makinede ~35 bin istek/sn'de p99 < 4 ms kalıyor, yani gateway darboğaz değil. Gerçek ağ, ayrı makineler ve gerçek backend gecikmesiyle ölçülmedi.
Aracı: [bench/NetRoute.LoadTest](bench/NetRoute.LoadTest).

## Testler

```bash
dotnet test --solution NetRoute.slnx    # Redis testleri için Docker gerekir (Testcontainers)
```

43 test: token bucket (sınır, dolum, eşzamanlılık, bellek temizliği), Redis Lua betiği (iki node, TTL, kesinti), kesici/fallback
modları, JWT/güvenlik, uçtan uca rota/limit/yük dengeleme/failover ve admin API. Docker yoksa Redis testleri **atlanmaz, başarısız olur**.

## Mimari

```
istemci ─▶ [Kimlik doğrulama (JWT)] ─▶ [RateLimitMiddleware] ─▶ [Yetkilendirme] ─▶ YARP ─▶ backend'ler
                                          │                                         ▲
                                          ▼                                         │
                                  Redis (Lua token bucket)             StateStore ──┘ (canlı rota/cluster)
                                  └ kesinti: kesici → yerel limiter         ▲
                                                                     Admin API
```

Kararlar: [docs/adr](docs/adr).

## Bilinen sınırlar (dürüst liste)

- Admin API değişiklikleri **node başına** yerel dosyada tutulur; birden çok gateway node'u arasında yapılandırma senkronize edilmez
  (rate limit durumu paylaşılır, rota/politika değil). Üretimde yapılandırmanın tek kaynaktan (Git, Redis, config servisi) dağıtılması gerekir.
- `LocalFallback` modunda Redis kesintisinde toplam limit `node sayısı × limit` olur; bilerek seçilmiş bir ödünleşim.
- JWT doğrulaması testlerde simetrik anahtarla yapıldı; gerçek bir OIDC sağlayıcıya (`Authority`) karşı **denenmedi**.
- Pasif sağlık kontrolü yalnızca taşıma hatalarını (bağlantı/zaman aşımı) sayar, 5xx dönen ama ayakta olan backend'i çıkarmaz. İstek yeniden denemesi (retry) yok.
- Tek Redis örneğiyle test edildi; Redis Cluster/Sentinel denenmedi (betik tek anahtar kullandığı için küme uyumlu olması beklenir).
- `/dev/token` yalnızca `Development` ortamında vardır; docker-compose bilerek bu ortamda çalışır.
- Performans ölçümü yukarıdaki sınırlarla tek makinede yapıldı.
