# بروتوكول الاستقبال الحالي | AboEzzat Cast ↔ Receiver

هذه الملاحظات مستخرجة من `ReceiverServerService.java` في المستودع الأصلي. الغرض منها توثيق التوافق قبل تنفيذ أي منصة جديدة.

## Receiver الحالي

- HTTP listener على المنفذ الافتراضي `8081` (يمكن تغييره من إعدادات الريسيفر).
- اكتشاف UDP multicast على المجموعة `239.255.42.99` والمنفذ `37020`.
- رسالة الإعلان كل نحو 1.2 ثانية بالشكل:
  `ABOEZZAT_RECEIVER|<LAN-IP>|<port>`

## HTTP endpoints التي يقبلها الريسيفر

### إرسال رابط فيديو أو صورة

`POST /play`

Body JSON مثال:

```json
{
  "url": "https://example.com/media.mp4",
  "headers": {
    "user-agent": "...",
    "referer": "..."
  }
}
```

الريسيفر يقبل URL من نوع HTTP/HTTPS فقط. رؤوس الطلب المسموح تمريرها للمشغل: `user-agent`, `referer`, `origin`, `accept`, `accept-language`, `cookie` مع حدود طول ومنع CR/LF.

### أوامر عبر query string

- `/?command=ping`
- `/?command=status`
- `/?command=stop`
- `/?command=play_pause`
- `/?command=rw`
- `/?command=ff`
- `/?command=next`
- `/?command=prev`
- `/?command=volume_up`
- `/?command=volume_down`
- `/?command=mute`

كما يقبل رابط الوسائط من query parameter باسم `url` أو `video`، والصوت باسم `audio`.

> هذه قائمة بالأوامر التي يجب مطابقتها مع كود AboEzzat Cast قبل اعتمادها كعقد API نهائي؛ بعض الأوامر يتم التعامل معها داخل MainActivity.

## ملاحظات توافق المنصات الجديدة

1. لا تغيّر المنفذ أو صيغة JSON أو أسماء الأوامر في AboEzzat Cast الأصلي دون خطة توافق واختبار.
2. اكتشاف UDP multicast قد لا يكون متاحًا أو موثوقًا في تطبيقات الويب على التلفزيون؛ يلزم اختبار عملي.
3. تطبيقات TV والويب قد لا تقدر على فتح HTTP listener مثل Android Service. إذا لم يتوفر ذلك، نحتاج مسارًا بديلًا (مثل polling/relay initiated by TV) أو إضافة منفصلة إلى Cast.
4. لا نعتبر الاكتشاف أو التشغيل متوافقًا حتى يتم اختبار إرسال حقيقي من AboEzzat Cast على الجهاز المستهدف.

## المصدر

- [ReceiverServerService.java](https://github.com/mohamedezzat19/AboEzzat-Receiver/blob/main/app/src/main/java/com/aboezzat/receiver/ReceiverServerService.java)
- [AboEzzat Cast](https://github.com/mohamedezzat19/AboEzzat-Cast)
