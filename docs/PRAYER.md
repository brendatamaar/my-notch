# Prayer

Native 600 DIP-wide page with a height of 202 DIPs inspired primarily by the supplied Oyenza reference, with a blue timeline inspired by Nusa. Left: next prayer, live countdown, city/timezone, Change place. Right: sunrise, five daily times, dotted curve, elapsed blue line and a larger day/night marker. The curve represents progression between prayer times, not solar elevation.

Ten Indonesian city/regency presets carry coordinates and WIB/WITA/WIT. Selection persists in `%LOCALAPPDATA%/MyNotch/prayer-location.txt`; switching clears the old schedule and stale responses cannot overwrite a newer location. Aladhan method 20 (Kemenag calculation) is requested with the city timezone. This is a calculated schedule, not an official published local timetable. After Isha, tomorrow's Fajr is fetched separately. Sunrise is informational, never the next prayer reminder.

A one-shot reminder timer handles the next prayer. Countdown updates only while the Prayer page is visible; network calls occur on startup, location changes, prayer boundaries, date changes or reopening after failure. No audio/Adzan controls are shown because audio playback is not implemented.

Verification: dependency-free harness covers next-prayer boundaries, rollover, countdown, arc bounds/monotonicity and city timezone conversion. Manual check: open Prayer; change city; confirm loading clears old timings, city persists after restart, countdown advances, and other pages remain unchanged.

Location is manually selected, not fetched from Windows or IP geolocation. The initial fallback is DKI Jakarta. Kota Bogor and Kabupaten Bogor are separate presets; the regency uses its administrative centre, Cibinong. Administrative labels appear directly in the city text. The 24-unit vector marker switches between four fixed local-clock periods: Pagi 05:00-10:59 (sunrise icon), Siang 11:00-14:59 (full sun), Sore 15:00-17:59 (sunset icon), Malam 18:00-04:59 (crescent). These are day-period illustrations, independent of astronomical sunrise and Maghrib. Change place uses the same plain button style as Focus with no arrow.

API: https://aladhan.com/prayer-times-api

Sky marker and halo render in an unscaled overlay: curve coordinates map to the rendered host, while the marker stays 24 x 24 DIPs and its halo stays 30 x 30 DIPs. Curve resizing therefore cannot stretch the sun or crescent.

Above the curve, Sunrise, Imsak and Qibla share a compact row. Imsak is read from Aladhan timings (optional; --:-- if absent/invalid), never inferred by subtracting minutes from Subuh and never used as a prayer reminder. Qibla is the initial great-circle bearing from the selected coordinates to the Kaaba (21.4225 N, 39.8262 E), normalized clockwise from true north; it is not a live device compass.

Layout update: this tab is 600 x 202 DIPs and independent of AI usage height. Main tabs have no page scrolling.

Marker visual update: warm filled sun with short fine rays, simple sunrise/sunset horizon silhouettes with no arrows, and a lavender filled crescent. The old dark circular badge is replaced by a transparent 24 x 24 DIP glyph layer and a soft 28 DIP radial glow tinted by day period. A small backing masks the timeline beneath the glyph. Dimensions remain independent of curve scaling.
