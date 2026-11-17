# BR-Libretro 0.6.12

- Normalizes No-Intro trailing articles, such as `Legend of Zelda, The`, to natural title order.
- Treats punctuation and diacritic differences as equivalent when validating unique IGDB exact-title matches.
- Keeps the safety rule that bulk matching only applies one unique canonical title match.
- Removes additional handheld dump tags such as `SGB Enhanced` from media labels and searches.
- Logs the returned candidate names when a bulk match still requires manual review.
- Adds a paged review list that lets users search and select matches for every unmatched ROM.
- Opens the review list automatically when bulk matching leaves ambiguous results.
