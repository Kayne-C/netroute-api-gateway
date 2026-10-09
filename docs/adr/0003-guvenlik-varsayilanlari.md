# ADR 0003 — Güvenlik varsayılanları

**Durum:** Kabul edildi

- **Varsayılan kapalı:** `FallbackPolicy = RequireAuthenticatedUser`. Rota unutulan bir yetkilendirme ayarıyla yanlışlıkla açık kalmaz;
  herkese açık rota `AuthorizationPolicy: "anonymous"` ile açıkça işaretlenir.
- **Kimlik başlıkları:** `X-User-Id`, `X-User-Tier`, `X-User-Roles` yalnızca gateway tarafından, doğrulanmış token'dan yazılır; istemcinin gönderdiği aynı adlı
  başlıklar her istekte silinir. Aksi halde gateway'e güvenen backend'lere karşı kimlik taklidi mümkün olur.
- **Sıralama:** kimlik doğrulama → rate limit → yetkilendirme. Reddedilen/anonim istek seli de kısılır (IP başına).
- **Gateway başlangıçta doğrular:** ne `Authority` ne `SigningKey` varsa süreç başlamaz; simetrik anahtar yalnızca `Development`'ta kabul edilir (min. 32 karakter, yalnız HS256).
- **Admin API** ayrı `admin` rol politikasına bağlıdır; her yazma YARP doğrulayıcısından ve gateway'in çapraz kontrollerinden (bilinmeyen cluster/politika) geçer.
