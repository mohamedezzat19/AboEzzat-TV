# تمرير ترويسات HTTP إلى مشغل Windows

## الحالة الحالية — 27 نوفمبر 2026

طبقة الوسيط المحلية **منفذة** في `windows/MediaHeaderProxy.cs` ومشمولة في Windows Installer Actions #84 على commit `e17e1a21dd9faf2d86c41aacf5fde0048a11c67e`.

- [بناء Windows #84](https://github.com/mohamedezzat19/AboEzzat-TV/actions/runs/45970631865)
- [مصدر MediaHeaderProxy.cs](../windows/MediaHeaderProxy.cs)

نجاح البناء يثبت التجميع والنشر وإنشاء المثبّت فقط؛ لا يثبت نجاح تشغيل MP4 أو HLS أو وصول كل الترويسات إلى خادم بث حقيقي.

## لماذا توجد الطبقة؟

يستخدم المشروع `LibVLCSharp.WPF 3.10.2` و`VideoLAN.LibVLC.Windows 3.0.24`. خيارات LibVLC الحالية في `VlcPlaybackPrototype.xaml.cs` تشمل User-Agent وReferer، لكن لا ينبغي افتراض أن خيارًا غير موثق مثل `:http-header=...` سيطبق ترويسات اعتباطية على طلبات HLS التابعة.

لذلك يمرر Windows بعض الوسائط عبر HTTP proxy محلي على Loopback قبل تسليمها إلى LibVLC.

## ما ينفذه الكود حاليًا

1. ينشئ `CreateSession` جلسة ذات معرّف عشوائي، ويشغّل خادم TCP على `127.0.0.1` ومنفذ محلي عشوائي.
2. يقبل الوسيط طلبات GET وHEAD فقط، بحد أقصى 24 عميلًا متزامنًا، و32 KiB لترويسات الطلب المحلي، ومهلة 15 ثانية لقراءة الطلب المحلي.
3. يجلب المصدر عبر HTTP/HTTPS باستخدام `HttpClient` مع إيقاف التحويل التلقائي؛ ويتبع حتى 8 تحويلات HTTP صراحةً، مع مهلة 45 ثانية للحصول على ترويسات استجابة المصدر.
4. يمرر ترويسات المصدر المسموح بها مثل User-Agent وReferer وOrigin وAccept وAccept-Language، ويستبعد ترويسات hop-by-hop.
5. يقيّد Cookie إلى الأصل الذي استُخرج من Origin، أو Referer عند غياب Origin، أو أصل رابط الوسائط عند غيابهما. وتُقيّد Authorization ومفاتيح API والترويسات الحساسة الأخرى إلى أصل رابط الوسائط.
6. يمرر Range وIf-Range وIf-None-Match وIf-Modified-Since، ويعيد بعض ترويسات الاستجابة المهمة مثل Content-Range وAccept-Ranges وETag وLast-Modified.
7. يعيد كتابة روابط HLS النسبية والمطلقة الموجودة في أسطر الوسائط وسمات URI داخل الوسوم إلى عناوين Loopback؛ ويشمل ذلك القوائم الفرعية والمقاطع ومفاتيح التشفير وخرائط التهيئة عندما تكون ممثلة بهذه الصيغ.
8. تُزال جلسة الوسيط عند التخلص من كائن Session الذي يملكه مسار التشغيل.

## حدود معروفة ومخاطر لم تُحل بعد

- **حد حجم HLS ليس حدًا أثناء القراءة:** الكود يستعمل حاليًا `ReadAsByteArrayAsync` ثم يفحص الحجم مقابل 4 MiB. لذلك قد تكون القائمة الأكبر قد حُمّلت بالفعل إلى الذاكرة قبل رفضها. يجب تطبيق الحد أثناء القراءة من Stream.
- **حماية SSRF غير مكتملة:** التحقق الحالي `IsHttpUri` يرفض المخططات غير HTTP/HTTPS فقط. التحويلات وروابط HLS التابعة يمكن أن تشير إلى loopback أو عناوين خاصة/داخلية؛ لا توجد حتى الآن سياسة تحقق من عناوين IP بعد DNS أو من وجهة كل تحويل. يجب إضافة سياسة وجهات آمنة، مع قرار صريح حول السماح بمصادر LAN التي يرسلها المستخدم عمدًا، قبل اعتبار الوسيط آمنًا للاستخدام الواسع.
- لا توجد إعادة كتابة DASH/MPD.
- لا يعيد الوسيط حاليًا تمرير كل ترويسات الاستجابة، ومنها Set-Cookie، ولا يدير Cookie jar تلقائيًا.
- سياسة Cookie الحالية محافظة: لا ترسل Cookie إلى CDN مختلف لمجرد أن المصدر أعاد توجيه الطلب إليه. قد تتطلب بعض الخدمات سياسة نطاقات موثقة بدل توسيع الإرسال عشوائيًا.
- دعم Range مرّره الكود وأعاد بعض الترويسات، لكن لم يثبت بعد باختبار خادم HTTP تجريبي أن استجابة 206 وContent-Range وحالات 416 تعمل كما ينبغي.
- لا توجد اختبارات وظيفية آلية مستقلة لـMediaHeaderProxy في المستودع حتى هذا التحديث. Workflow المسمى `Windows installer verification` يبني المثبّت ويرفع Artifact مرة أخرى؛ لا ينفذ اختبارات Proxy.

## الاختبارات المطلوبة قبل إعلان التوافق

- خادم HTTP محلي تجريبي يتحقق من وصول Origin وReferer وAccept وAccept-Language وUser-Agent وCookie وAuthorization، مع التأكد من عدم تسريب الترويسات الحساسة إلى أصل آخر بعد التحويل.
- MP4: طلب Range، واستجابة 206، وContent-Range، وIf-Range، وحالة 416.
- HLS: Master playlist، variant playlist، segments، EXT-X-KEY، EXT-X-MAP، URI نسبي ومطلق، وتحويلات HTTP.
- قائمة أكبر من 4 MiB: التأكد من إيقاف القراءة فور تجاوز الحد دون تحميل الملف كله.
- رفض تحويل أو رابط HLS تابع إلى loopback أو عنوان داخلي غير مصرح به، مع اختبار مصادر LAN المصرح بها صراحة.
- إلغاء الجلسة عند تغيير الفيديو أو إيقافه، وقطع عميل بطيء، وعدم تسريب الموارد بعد الإلغاء.

## المراجع

- LibVLCSharp Media.AddOption: https://docs.videolan.me/libvlcsharp/api/LibVLCSharp.Shared.Media.html
- إرشادات خيارات LibVLC: https://docs.videolan.me/libvlcsharp/docs/how_do_I_do_X.html
- LibVLC media API: https://videolan.videolan.me/vlc/master/group__libvlc__media.html
