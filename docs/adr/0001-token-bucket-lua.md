# ADR 0001 — Dağıtık rate limiting: Redis'te tek Lua betiğiyle token bucket

**Durum:** Kabul edildi

## Bağlam

Birden çok gateway node'u aynı istemciye tek bir limit uygulamalı. İstemci başına bir "oku → hesapla → yaz" döngüsü
uygulama tarafında yapılırsa iki node aynı anda okuyup aynı token'ı harcar (yarış), limit aşılır.

## Karar

- Algoritma: **token bucket** (patlama kapasitesi + sürekli dolum hızı). Sabit pencerede sınır anındaki 2x patlama sorunu yok,
  kayan pencere günlüğüne göre anahtar başına O(1) bellek.
- Okuma-dolum-harcama tek **Lua betiği**dir; Redis betiği tek iş parçacığında atomik çalıştırır. Test: iki node, 4000 eşzamanlı istek, 200 token → tam 200 kabul.
- Zaman olarak betik içinde Redis `TIME` kullanılır; gateway node'larının saat farkı sonucu etkilemez.
- Anahtarlara `PEXPIRE` (kapasite/dolum + 1 sn) konur: bucket dolduğunda zaten yeni gibidir, kaybı yok, Redis büyümez.
- Anahtar: `{politika}:{özne}`; özne = JWT `sub`, yoksa `X-Api-Key`'in SHA-256 özeti (ham anahtar saklanmaz), yoksa istemci IP'si.

## Sonuçlar

- Her limitli istek bir Redis gidiş-dönüşü ekler (ölçüm README'de). Bu yüzden 50 ms zaman aşımı ve kesici var (ADR 0002).
- Politika değişince mevcut bucket'lar token'larını korur, yeni kapasiteye kırpılır/yeni hızla dolar.
