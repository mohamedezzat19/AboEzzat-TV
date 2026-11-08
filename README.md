# أبو عزت ريسيفر للويندوز | AboEzzat Receiver Windows

هدف المشروع هو نقل تجربة وإمكانيات **AboEzzat Receiver Android** إلى Windows، مع إبقاء تطبيق Android وAboEzzat Cast دون أي تعديل.

## الحالة الحالية

أُضيفت بداية مشروع Windows WPF تحت مجلد `windows/`:
- `AboEzzatReceiver.Windows.csproj`
- `App.xaml` و`App.xaml.cs`
- `MainWindow.xaml` و`MainWindow.xaml.cs`

هذه **نسخة أولية غير مكتملة وغير مختبرة بالبناء أو الاتصال الفعلي مع Cast**. لا تعتبر متوافقة نهائيًا مع AboEzzat Cast بعد.

## تشغيل المشروع

المتطلبات: Windows و.NET 8 SDK مع Windows Desktop/WPF.

من PowerShell داخل مجلد المشروع:

```powershell
cd windows
dotnet build
dotnet run
```

## ما هو موجود في النموذج الأولي

- نافذة WPF عربية بهوية لونية داكنة مبدئية.
- بطاقة حالة تعرض عنوان IPv4 والمنفذ 8081.
- مساحة عرض فيديو وصور.
- زر إيقاف وزر ملء الشاشة.
- خادم HTTP تجريبي على المنفذ 8081 مع مسارات اختبار عامة `/play?url=` و`/image?url=`.

## مهم قبل التوافق مع AboEzzat Cast

المسارات السابقة للتجربة فقط، وليست تأكيدًا لبروتوكول Cast. يجب أولًا استخراج مسارات HTTP الفعلية وطريقة إرسال البيانات من AboEzzat Receiver Android، ثم تنفيذها كما هي في Windows. كما يجب مراجعة اكتشاف الأجهزة عبر UDP 37020 قبل إعلان التوافق.

## خطة نقل الميزات

1. مطابقة بروتوكول HTTP وUDP الحالي مع Android Receiver.
2. تشغيل فيديو جديد عند كل طلب وعدم إعادة الفيديو السابق.
3. اختبار الصور والفيديو وHLS والروابط التي يدعمها Windows.
4. نقل واجهة Android الأصلية وخلفيتها وأصولها المرئية إلى Windows.
5. نقل إعدادات المتصفح والترجمة والسجل وباقي الوظائف بعد تحديد اعتمادياتها.
6. بناء نسخة Release واختبارها فعليًا مع AboEzzat Cast.

## الحفاظ على التطبيقات الأصلية

لن يتم تعديل مستودع AboEzzat Receiver Android أو AboEzzat Cast ضمن هذا المشروع.
