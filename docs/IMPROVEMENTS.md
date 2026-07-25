# تحسينات HerdMe المقترحة

مراجعة تقنية شاملة للمشروع: الباك اند (macOS/Swift، Windows/C#، النواة C++)،
الفرنت اند (SwiftUI و WinUI)، والإضافات المقترحة.

آخر تحديث: 2026-07-25 — بناءً على فحص 157 ملفًا مُتتبعًا في Git
(~6,200 سطر Swift خدمات، ~2,500 سطر SwiftUI، ~7,100 سطر C#، 413 سطر C++).

> **حالة التنفيذ بعد المراجعة — 2026-07-25:** تم تنفيذ B-1 وB-5 وB-6 وB-12
> وI-1 وإضافة `SECURITY.md`. تم توحيد كل استدعاءات `Process` في B-11 داخل
> `ProcessRunner` بقراءة متزامنة للمخرجات ومهلة وإنهاء للعملية وسقف ذاكرة؛ يبقى
> ربط الإلغاء التعاوني من واجهة المستخدم تحسينًا لاحقًا. تم تنفيذ B-39 بحيث
> يحاول Windows المنفذين 80/443 ثم يتراجع إلى 8080/8443، ونجحت العقود العابرة
> للمنصات، بينما يبقى التحقق النهائي على جهاز Windows فعلي مطلوبًا.

---

## المحتويات

1. [ملخص تنفيذي](#1-ملخص-تنفيذي)
2. [تحسينات الباك اند](#2-تحسينات-الباك-اند)
3. [تحسينات الفرنت اند](#3-تحسينات-الفرنت-اند)
4. [الإضافات المقترحة](#4-الإضافات-المقترحة)
5. [البنية التحتية والإصدار](#5-البنية-التحتية-والإصدار)
6. [خطة التنفيذ المقترحة](#6-خطة-التنفيذ-المقترحة)

---

## 1. ملخص تنفيذي

### نقاط القوة الحالية

المشروع في حالة جيدة أكثر مما يبدو من حجمه. تحققت من الأمور التالية فعليًا في الكود:

- **كل المنافذ محلية**: لا توجد أي خدمة تستمع على `0.0.0.0`. جميع المستمعين
  (FastCGI، HTTP/HTTPS، DNS، SMTP، Dumps، وكل خدمات قواعد البيانات) مقيّدون
  بـ `127.0.0.1`، وPHP-FPM يستخدم `listen.allowed_clients = 127.0.0.1`.
- **حماية من Path Traversal**: `LocalFastCGIGateway.swift` و
  `LocalHttpSiteServer.cs:263` يحلّان الروابط الرمزية ويتحققان من الاحتواء داخل
  جذر الموقع، وهذا مُغطى باختبارات.
- **تحقق من سلامة التنزيلات على Windows**: PHP وNode وComposer وXdebug وكل
  الخدمات تتحقق من SHA-256 قبل التثبيت.
- **مساحة اختبارات محترمة**: 78 اختبار XCTest على macOS و~100 تحقق تعاقدي على
  Windows، مع بوابة حقيقية لإنشاء وتشغيل مشروع Laravel.
- **نظافة المستودع**: لا توجد ملفات بناء مُتتبعة في Git ولا `.DS_Store`.

### أهم 10 مشاكل مرتّبة بالأولوية

| # | المشكلة | الطبقة | الخطورة |
|---|---------|--------|---------|
| 1 | تنزيل Node.js على macOS بدون أي تحقق من البصمة | باك اند macOS | حرجة |
| 2 | تجميد محتمل (deadlock) في كل استدعاءات `Process` بسبب أنابيب ممتلئة | باك اند macOS | حرجة |
| 3 | خادم SMTP يعلن حدًا 50MB ولا يفرضه → استهلاك ذاكرة غير محدود | باك اند macOS | حرجة |
| 4 | لا يوجد CI لـ macOS إطلاقًا (78 اختبارًا لا تعمل آليًا) | البنية التحتية | حرجة |
| 5 | `AppModel` كائن عملاق: 1,353 سطر و42 خاصية `@Published` | فرنت اند macOS | عالية |
| 6 | `LogsView` يعيد قراءة الملف كاملًا كل ثانية على الـ main thread | فرنت اند macOS | عالية |
| 7 | Windows لا يستخدم المنفذين 80/443 → المستخدم مضطر لكتابة `:8443` | باك اند Windows | عالية |
| 8 | لا يوجد تعريب مطلقًا (صفر مطابقة لـ `LocalizedStringKey`) | فرنت اند | عالية |
| 9 | Xdebug على macOS يُنزّل من PECL بدون تحقق من البصمة | باك اند macOS | عالية |
| 10 | النواة C++ لا تُستخدم على macOS إطلاقًا خلافًا لما يقوله README | معمارية | عالية |

### الفكرة المركزية

> النواة المشتركة `Core/` (413 سطر C++) تُستخدم من Windows فقط عبر
> استدعاء `herdme-core.exe` كعملية فرعية. **macOS لا يبنيها ولا يستدعيها ولا
> يحزمها** — لا يوجد أي ذكر لـ `herdme-core` في `HerdMe/` أو `project.yml`.
> والنتيجة أن نفس المنطق مكتوب مرتين أو ثلاثًا، وقد بدأ يتباعد فعليًا.

---

## 2. تحسينات الباك اند

### 2.1 الأمان

#### [حرجة] B-1: تنزيل Node.js بدون تحقق من البصمة (macOS)

`HerdMe/Services/RuntimeInstaller.swift:157-165` — يُنزّل الأرشيف ويفكّه مباشرة:

```swift
let downloadURL = URL(string: "https://nodejs.org/dist/\(release.version)/\(archiveName)")!
let (temporaryArchive, downloadResponse) = try await URLSession.shared.download(from: downloadURL)
guard (downloadResponse as? HTTPURLResponse)?.statusCode == 200 else {
    throw RuntimeInstallationError.invalidResponse
}
try unpack(archive: temporaryArchive, into: staging)   // ← لا تحقق من البصمة
```

المفارقة أن نسخة Windows في `NodeRuntimeInstaller.cs:190` **تتحقق** فعلًا
(`await DownloadAndVerifyAsync(...)`). أي أن macOS أضعف أمنيًا من Windows في نفس الميزة.

**الإصلاح**: تنزيل `SHASUMS256.txt` من نفس مجلد الإصدار، والتحقق من بصمة
الأرشيف قبل `unpack`، مع الفشل المُغلق (fail-closed) عند عدم التطابق. ويُفضّل
التحقق من `SHASUMS256.txt.sig` بمفتاح Node.js الرسمي.

#### [عالية] B-2: Xdebug من PECL بدون تحقق

`HerdMe/Services/XdebugManager.swift:190-217` — يُنزّل `.tgz` من `pecl.php.net`،
يُترجمه، ويُحمّله كـ `zend_extension`. رقم الإصدار يُتحقق منه بـ regex لكن محتوى
الأرشيف موثوق بالكامل. نسخة Windows تستخدم بصمة SHA-256 من GitHub Releases.

**الإصلاح**: مواءمة macOS مع نموذج Windows (أرشيفات GitHub مع SHA-256)، أو
التحقق من بصمات PECL المنشورة.

#### [عالية] B-3: تحديثات التطبيق بدون توقيع

`HerdMe/Services/AppUpdateManager.swift:39-46` — `HERDME_UPDATE_FEED_URL` يسمح
بتوجيه التطبيق لأي عنوان، والبيان (manifest) يُفكّ ترميزه من JSON بدون أي تحقق من
الأصالة، ثم يُفتح رابط التنزيل عبر `NSWorkspace`. نفس المشكلة على Windows
(`AppUpdateManager.cs:63-104`).

**الإصلاح**: تجاهل متغير البيئة في إصدارات Release، توقيع البيانات بـ Ed25519
والتحقق قبل العرض، أو الانتقال إلى Sparkle على macOS.

#### [عالية] B-4: استخدام API مهجورة للصلاحيات المرتفعة

`HerdMe/Services/PrivilegedCommandRunner.swift:84-98` — يستخدم
`AuthorizationExecuteWithPrivileges` عبر `dlsym`. هذه API مهجورة منذ سنوات، ولا
تُرجع مخرجات ولا حالة خروج الأمر — فقط حالة الإطلاق. أي فشل في `install` أو
`launchctl bootstrap` قد يمرّ بصمت (انظر أيضًا B-11).

**الإصلاح**: مساعد `LaunchDaemon` عبر SMJobBless + XPC مع سطح أوامر محدود ومُدقّق.
كحدّ أدنى: التحقق من حالة الخروج والتحقق اللاحق (`launchctl print`، وجود الملف).

#### [عالية] B-5: SMTP يعلن حدًا ولا يفرضه

`HerdMe/Services/SMTPServer.swift:125` يُعلن `250 SIZE 52428800`، لكن
`SMTPServer.swift:118` يُضيف الأسطر بلا حدّ:

```swift
} else {
    dataLines.append(line)   // ← بلا أي عدّ للبايتات
}
```

نسخة Windows (`MailCaptureService.cs:206`) تفرض الحد بشكل صحيح.

**الإصلاح**: تتبّع عدد البايتات التراكمي والرفض بـ `552` عند تجاوز 50MB، وتحديد
سقف لنمو `buffer` داخل `receive()`.

#### [متوسطة] B-6: حدود مفقودة في مُحلّلات الإدخال غير الموثوق

- `PHPSerializationParser.swift:108-130`: يقرأ `count` من الحمولة ويكرّر بلا سقف،
  و`rendered(depth:)` بلا حدّ عمق → استهلاك ذاكرة/تعمّق مكدس من حمولة خبيثة.
- `DumpCaptureServer.swift:76-97`: `buffer` يتراكم بلا سقف إجمالي (فقط كل قراءة
  محدودة بـ 1MB).

**الإصلاح**: سقوف صريحة (10,000 عنصر، عمق 32، 4MB لكل اتصال) ورفض الاتصال عند التجاوز.

#### [متوسطة] B-7: عرض HTML للبريد بـ `unsafe-inline`

`MailMIMEParser.swift:59-67` يُدرج HTML الخام داخل `<body>`، وCSP يسمح بـ
`style-src 'unsafe-inline'`. JavaScript مُعطّل (جيد، `MailHTMLPreview.swift:11`)
لكن يبقى تسريب عبر CSS وإعادة تشكيل الواجهة.

**الإصلاح**: تنقية HTML بقائمة سماح للعناصر والسمات قبل الإدراج، وتضييق CSS
إلى `'none'` أو تقديم النص العادي افتراضيًا.

#### [متوسطة] B-8: بيانات اعتماد ثابتة ومكشوفة

- `ServiceProcessManager.swift:431-434`: MinIO بـ `herdme/herdme-local-service`
  (ونفسها في `WindowsServiceManager.cs:336`)، وTypesense بـ `--api-key herdme-local-service`.
- `LocalCertificateManager.swift:42`: كلمة مرور PKCS#12 مكتوبة في الكود.
- قواعد البيانات تُهيّأ بلا مصادقة (`--initialize-insecure`، `initdb --auth=trust`).

**الإصلاح**: توليد بيانات اعتماد عشوائية لكل تثبيت وتخزينها في Keychain /
Credential Manager. المخاطرة مقبولة على loopback لكن يجب توثيق نموذج التهديد.

#### [متوسطة] B-9: مفتاح CA الخاص على القرص

`LocalCertificateManager.swift:48` (و`WindowsCertificateManager.cs:113`) يخزّن مفتاح
CA بصلاحيات `0600` في مجلد قابل للكتابة من المستخدم. أي عملية تعمل بنفس حساب
المستخدم يمكنها إصدار شهادات لأي نطاق `*.tld`.

**الإصلاح**: تخزين المفتاح في Keychain / User store مع تقوية ACL.

#### [متوسطة] B-10: فكّ الأرشيف بدون تحقق من المسارات

- macOS: `RuntimeInstaller.swift:724` و`XdebugManager.swift:215` يستخدمان
  `tar -xzf --strip-components 1` بدون تدقيق لاحق للمسارات.
- Windows: `ZipFile.ExtractToDirectory` في `ServicePackageInstaller.cs:98`،
  `PhpRuntimeInstaller.cs:147`، `NodeRuntimeInstaller.cs:191`.

**ملاحظة دقيقة**: .NET يمنع فعليًا تجاوز المسار بـ `../` داخل
`ExtractToDirectory`، فالخطر هنا أقل مما يبدو. الفجوة المتبقية على Windows هي
غياب حدود قنابل الـ zip (عدد المدخلات، الحجم المفكوك). أما على macOS فـ `tar`
لا يوفّر هذه الحماية تلقائيًا.

**الإصلاح**: على macOS، تدقيق أن كل الملفات المستخرجة تحت جذر الـ staging.
على الجانبين: سقف لعدد المدخلات والحجم الإجمالي المفكوك. النمط الصحيح موجود
بالفعل في `XdebugManager.cs:251` (يستخرج المدخل المتوقع فقط).

---

### 2.2 التزامن والصحة

#### [حرجة] B-11: تجميد محتمل في كل استدعاءات `Process`

نمط متكرر في **14 موضعًا موزّعة على 10 ملفات** (تحققت بالعدّ:
`ProjectCreator` 4، `RuntimeInstaller` 2، وموضع واحد في كلٍّ من
`ServiceProcessManager`، `XdebugManager`، `PHPRuntimeValidator`، `PHPFPMManager`،
`DomainResolverManager`، `LocalCertificateManager`، `RuntimeInspector`، `AppModel`):

```swift
try process.run()
let data = pipe.fileHandleForReading.readDataToEndOfFile()
process.waitUntilExit()
```

إذا تجاوز مخرج العملية الابنة سعة الأنبوب (~64KB) قبل أن يقرأه الأب، تتوقف
الابنة عند الكتابة ويتوقف الأب عند القراءة — تجميد كلاسيكي. هذا يمسّ
`brew install` و`laravel new` و`npm install` و`composer global require` — وكلها
تُنتج مخرجات كبيرة جدًا.

**الإصلاح**: نوع مشترك `ProcessRunner` يقرأ stdout/stderr بشكل غير متزامن على
طابور خلفي أثناء الانتظار، مع مهلة زمنية، وإلغاء (cancellation)، وبثّ المخرجات
للسجل. هذا يحلّ B-11 وB-13 وB-15 معًا ويجعل الطبقة قابلة للاختبار.

#### [عالية] B-12: تسريب جلسات في SMTP و Dumps

`SMTPServer.swift:47-51` و`DumpCaptureServer.swift:46-50` يُضيفان الجلسة إلى
المصفوفة ولا يحذفانها عند انتهائها (تُفرَّغ فقط في `stop()`). النمط الصحيح
موجود بالفعل في `LocalFastCGIGateway` و`LocalHTTPProxy` عبر `onStop`.

**الإصلاح**: تمرير `onStop` لحذف الجلسة من المصفوفة.

#### [عالية] B-13: `@unchecked Sendable` مع أقفال غير كافية

`ServiceProcessManager.swift:46` مُعلَّم `@unchecked Sendable`، و`state(for:)`
يقرأ ويكتب `adoptedProcessIDs` عبر دورات قفل/فتح متعددة → تعارض ممكن بين
`start`/`stop`/`state` المتزامنة. و`consoleURL(for:)` يعدّل `consolePorts` داخل
القفل لكنه يستدعي مساعدات sysctl خارجه.

**الإصلاح**: تحويله إلى `actor`، أو الإمساك بالقفل لكامل الانتقال بين الحالات.

#### [عالية] B-14: حالة بيئة قديمة (stale)

`LocalEnvironmentEngine.swift:43-45` يعيد المنافذ مباشرة إذا كان `isRunning`
صحيحًا، بدون التحقق من أن البوابات والوكيل ما زالوا أحياء. انهيار PHP-FPM يترك
قاموس `gateways` قديمًا ومنافذ خاطئة.

**الإصلاح**: فحص صحة (health check) للاتصال بـ FPM وحالة المستمعين قبل الإرجاع
المبكر، وإعادة البناء عند الفشل.

#### [عالية] B-15: `async void` منتشر على Windows

معالجات الأحداث في `SitesPage.xaml.cs` (الأسطر 46، 53، 102، …)،
`ServicesPage.xaml.cs:25-172`، `GeneralPage.xaml.cs:42-258`،
`App.xaml.cs:101-124` كلها `async void`. أي استثناء غير مُعالج يُسقط العملية،
ولا يوجد تقرير أخطاء مركزي ولا دعم للإلغاء.

**الإصلاح**: `async Task` + معالج استثناءات عام، أو نمط `IAsyncRelayCommand`.

#### [متوسطة] B-16: `.Result` على خيط الواجهة

`PhpPage.xaml.cs:98-107` يستخدم `composerReleaseTask.Result` بعد
`await Task.WhenAll(...)`.

**الإصلاح**: التقاط القيم من نتيجة `WhenAll` أو الانتظار الفردي.

#### [متوسطة] B-17: حلقات انتظار حاجزة

`ServiceProcessManager.swift:439,591` و`PHPFPMManager.swift:112-115` تستخدم
`usleep` لمدة 1–3 ثوانٍ لكل عملية بدء/إيقاف، مما يحجز خيوط `Task.detached`.

**الإصلاح**: انتظار غير متزامن بـ `Task.sleep` مع تراجع تدريجي (backoff) وفحص
اتصال المنفذ.

#### [متوسطة] B-18: `HttpClient` بلا مهلة زمنية

كل عملاء HTTP الساكنين على Windows (`AppUpdateManager.cs:26`،
`ComposerToolManager.cs:11`، `PhpRuntimeInstaller.cs:17`،
`NodeRuntimeInstaller.cs:11`، `ServicePackageInstaller.cs:648`،
`XdebugManager.cs:262`) يستخدمون المهلة الافتراضية اللانهائية.

**الإصلاح**: `Timeout` صريح + Polly لإعادة المحاولة مع تراجع تدريجي.

#### [متوسطة] B-19: غياب الإلغاء في العمليات الطويلة

`ProjectCreator.swift:212-281` — `laravel new` و`npm install` و`npm run build`
لا يمكن إلغاؤها إذا أغلق المستخدم المعالج. والصفحات على Windows لا تمرّر
`CancellationToken` من دورة حياة الصفحة.

**الإصلاح**: `CancellationToken` كامل المسار، `process.terminate()` عند الإلغاء،
وتنظيف المجلد الجزئي.

---

### 2.3 الموثوقية

| المعرّف | المشكلة | الموقع | الإصلاح |
|---------|---------|--------|---------|
| B-20 | جاهزية الخدمة = نوم ثابت 120ms بلا فحص فعلي | `ServiceProcessManager.swift:438` | استقصاء جاهزية خاص بكل خدمة (اتصال TCP، ملف socket، علامة في السجل) مثل `PHPFPMManager.canConnect` |
| B-21 | إعدادات تالفة → إعادة تعيين صامتة للافتراضيات | `ConfigurationStore.swift:72-76` | نسخ الملف التالف احتياطيًا، إظهار الخطأ، وعدم الحفظ التلقائي بلا تأكيد |
| B-22 | لا استعادة للخدمات بعد انهيار التطبيق (Windows) | `WindowsServiceManager.cs:15,40-43` | تخزين PID/المنفذ دائمًا؛ عند البدء استقصاء المنافذ وتبنّي أو قتل العمليات المهجورة (موجود على macOS) |
| B-23 | لا مراقبة صحة لـ php-cgi (Windows) | `WindowsLocalEnvironment.cs` | مراقبة العمليات، إعادة التشغيل أو تعليم البيئة كمتدهورة |
| B-24 | عمليات `CoreClient` الفرعية خارج Job Object | `CoreClient.cs:120` | لفّها في job object أو مهلة + قتل |
| B-25 | لا تدوير للسجلات | `LogStore.swift:59-77` | تدوير عند 10MB + سقف عدد/عمر للبريد والـ dumps |
| B-26 | شهادة TLS جديدة عند كل تشغيل | `WindowsCertificateManager.cs:11-54` | تخزين الشهادة وإعادة التوليد فقط عند تغيّر النطاقات أو قرب الانتهاء |
| B-27 | لا إعادة محاولة للتنزيلات | `RuntimeInstaller.swift`, `XdebugManager.swift` | تراجع تدريجي على أخطاء 5xx والانتقالية |
| B-28 | مضيفو Windows لا يُحدَّثون عند إضافة موقع أثناء التشغيل | `WindowsLocalEnvironment.cs:85-88` | إعادة `EnsureMappingsAsync` بعد الفحص/الربط |

---

### 2.4 المعمارية

#### [عالية] B-29: النواة المشتركة ليست مشتركة

| المجال | النواة C++ | macOS (Swift) | Windows (C#) |
|--------|-----------|---------------|--------------|
| فحص المواقع | `scan_sites()` | `SiteScanner.swift` (نسخة مستقلة) | عبر `CoreClient` |
| تسمية DNS | `dns_label()` | `AppModels.swift:74-91` | عبر النواة |
| استثناء مسارات Herd | `belongs_to_other_herd()` | `ConfigurationStore.swift:3-40` | `SiteConfigurationStore.cs:145` + مُرشِّح مكرر في `CoreClient.cs:72` |
| امتدادات Laravel | `inspect_php_module_output()` | `PHPRuntimeValidator.swift:26-76` | عبر النواة |
| مقارنة الإصدارات | — | `RuntimeInstaller.isNewerVersion` | `RuntimeVersionComparison.cs` |
| كتالوج الخدمات | — | `ServiceCatalog.swift` | `ManagedServiceModels.cs` |
| تحليل MIME | — | `MailMIMEParser.swift` (197 سطرًا) | `MailMimeParser.cs` (162 سطرًا) |
| عميل FastCGI | — | `LocalFastCGIGateway.swift` (688 سطرًا) | `FastCgiClient.cs` (151 سطرًا) |

**التباعد بدأ فعلًا**، وهذا ليس خطرًا نظريًا:

1. **تحليل `php -m`**: النواة (`core.cpp:326`) تقرأ قسم `[PHP Modules]` فقط،
   بينما Swift (`PHPRuntimeValidator.swift:68`) يعتبر أي سطر لا يبدأ بـ `[`
   امتدادًا. أي أن التحقق من نفس PHP قد يعطي نتيجتين مختلفتين على المنصتين.
2. **إصدارات الكتالوج**: MariaDB `12.3` في Swift مقابل `11.8` في C#،
   وMongoDB `7.0` مقابل `8.0 LTS`.
3. **حدّ SMTP**: مفروض على Windows ومفقود على macOS (B-5).
4. **بصمة Node**: مفروضة على Windows ومفقودة على macOS (B-1).

**القرار المطلوب** — أحد خيارين، وكلاهما مقبول لكن الوضع الحالي ليس كذلك:

- **(أ) التبنّي**: بناء النواة كـ XCFramework بواجهة C ABI مستقرة واستدعاؤها من
  Swift، أو حزمها كـ CLI واستدعاؤها كعملية فرعية مثل Windows. ثم ترحيل المنطق
  الخالص إليها بالترتيب: تسمية DNS → امتدادات PHP → سياسة المسارات → مقارنة
  الإصدارات → كتالوج الخدمات → تحليل MIME → مُرمِّز FastCGI.
- **(ب) التوثيق الصادق**: تعديل `README.md:8-10` و`docs/PARITY.md:14-26` لتوضيح
  أن النواة طبقة تعاقد لـ Windows، وأن macOS يعيد التنفيذ أصليًا، مع **اختبارات
  متجهات مشتركة** (shared test vectors) تضمن عدم التباعد.

#### [عالية] B-30: كائنات عملاقة وغياب حقن التبعيات

| الملف | الأسطر | المسؤوليات المدمجة |
|-------|--------|-------------------|
| `LocalEnvironmentEngine.swift` | 223 | FPM + N بوابات + وكيلان + الشهادات + تخصيص المنافذ + التوجيه |
| `ServiceProcessManager.swift` | 863 | التثبيت + تهيئة قواعد البيانات + مواصفات الإطلاق + دورة الحياة |
| `LocalHttpSiteServer.cs` | 720 | تحليل HTTP + التوجيه + FastCGI + TLS + الملفات الساكنة |
| `ServicePackageInstaller.cs` | 652 | كل مُحلّلات إصدارات الخدمات + التثبيت |

لا توجد بروتوكولات/واجهات لأي منها، و`URLSession.shared` مستخدم مباشرة في كل
مكان، و`AppServices.cs:3-14` مُحدِّد موقع خدمات ساكن (service locator) بينما
الصفحات تُنشئ نسخًا موازية (`GeneralPage.xaml.cs:12` ينشئ
`new SiteConfigurationStore()` بدل المشتركة).

**الإصلاح**:
- تفكيك `LocalEnvironmentEngine` إلى `SiteRouter` + `PHPRuntimePool` +
  `TLSProxy` + `PortAllocator`.
- بروتوكولات `RuntimeInstalling` / `ProcessRunning` / `HTTPListening` للحقن والمحاكاة.
- على Windows: `Microsoft.Extensions.DependencyInjection` مع تسجيل في
  `App.xaml.cs` بدل المُحدِّد الساكن.

#### [متوسطة] B-31: قيم وإصدارات وعناوين مبثوثة في الكود

PHP 8.4 وNode 22 وPostgreSQL@18 وMongoDB 7.0 مبثوثة في `ServiceCatalog.swift:6-14`،
`AppModels.swift:311-313`، `ServiceProcessManager.swift:743`،
`ProjectCreator.swift:320`، وتكرارها على Windows. ومساعدات Homebrew مكرّرة بين
`ServiceProcessManager.swift:825-861` و`RuntimeInstaller.swift:688-722`.

**الإصلاح**: `RuntimeCatalog` واحد بصيغة JSON (مرشّح قوي للنقل إلى النواة)،
ونوع `HomebrewCLI` مشترك.

#### [متوسطة] B-32: ترحيل المخطط ضعيف

`ConfigurationStore.swift:51` يحتوي فقط على `independenceMigrationVersion = 1`؛
لا يوجد حقل إصدار عام للمخطط، والحقول الجديدة تُفكّ ترميزها بصمت إن وُجدت.

**الإصلاح**: `configSchemaVersion` + سلسلة ترحيل صريحة + تحقق عند الحفظ.

---

### 2.5 الأداء

| المعرّف | المشكلة | الموقع | الإصلاح |
|---------|---------|--------|---------|
| B-33 | الملفات الساكنة تُحمَّل كاملة في الذاكرة | `LocalFastCGIGateway.swift:471` | بثّ بالقطع + دعم `Range` (مهم للفيديو وأصول Vite الكبيرة) |
| B-34 | استجابة FastCGI مُخزَّنة بالكامل قبل الإرسال (سقف 64MB) | `LocalFastCGIGateway.swift:577-595` | بثّ سجلات stdout إلى جسم HTTP فور وصولها |
| B-35 | لا keep-alive | `LocalFastCGIGateway.swift:467`, `LocalHTTPProxy.swift` | keep-alive اختياري لوصلة الوكيل↔البوابة على loopback |
| B-36 | معالج FastCGI يعمل بالتزامن على طابور الجلسة | `LocalFastCGIGateway.swift:145` | نقل `handler.response(to:)` لمجمع الخيوط التعاوني |
| B-37 | `pm.max_children = 12` ثابت | `PHPFPMManager.swift:207` | تحجيم حسب عدد الأنوية أو جعله قابلًا للتهيئة |
| B-38 | `MailStore.load()` يقرأ كل الرسائل في كل تحميل | `MailStore.swift:12-21` | ملف فهرس أو تحميل كسول بالمعرّف |

---

### 2.6 فجوات التكافؤ على Windows

#### [عالية] B-39: لا توجد بوابة على المنفذين 80/443

macOS يحاول 80/443 أولًا (`LocalHTTPProxy.swift:17-30`). Windows يربط 8080/8443
مباشرة (`WindowsLocalEnvironment.cs:96-109`)، لكن ملف hosts يوجّه النطاق إلى
`127.0.0.1` فقط (`WindowsHostsManager.cs:216`). النتيجة: `https://site.test/`
بدون منفذ **لن يصل** إلى HerdMe، والمستخدم مضطر لكتابة `:8443`
(`SitePresentation.cs:33-38`).

هذه أكبر فجوة تجربة مستخدم على Windows.

**الإصلاح**: وكيل loopback على 80/443 يوجّه إلى المستمعين المُدارين (نفس نموذج
macOS)، أو محاولة 443/80 قبل التراجع.

#### فجوات أخرى

| المعرّف | الفجوة | التفصيل |
|---------|--------|---------|
| B-40 | لا خادم DNS محلي | Windows يعتمد على hosts فقط. `PARITY.md:45` يعدّ "كتلة hosts معزولة" مكافئة لـ DNS على macOS — وهذا مبالغة يجب تصحيحها |
| B-41 | Valkey وTypesense غير متاحين | `ManagedServiceModels.cs:22-40` — معطّلان بسبب غياب حزم Windows رسمية (مُوثَّق بأمانة) |
| B-42 | لا تكامل TablePlus | `TablePlusConnection.swift` غير مُنقول؛ `ServicesPage` يفتح وحدات MinIO/RustFS فقط |
| B-43 | لا استعادة تعارض MySQL/MariaDB | `DatabaseConflictRecoveryPlan` موجود على macOS فقط |
| B-44 | لا سجلات مُهيكلة | `File.AppendAllText` مباشر في `App.xaml.cs:143`، `WindowsServiceManager.cs:562`، `LocalHttpSiteServer.cs:544` → استخدام `Microsoft.Extensions.Logging` |
| B-45 | كتم الاستثناءات | `SitesPage.xaml.cs:575,590` (فشل المعاينة بصمت)، `WindowsServiceManager.LoadInstances:40` |
| B-46 | `explorer.exe` بمعامل غير آمن | `SitesPage.xaml.cs:638` يُدرج المسار في `FileName` بدل `ArgumentList` |

---

## 3. تحسينات الفرنت اند

### 3.1 إدارة الحالة والمعمارية

#### [عالية] F-1: `AppModel` كائن عملاق

`HerdMe/App/AppModel.swift` — 1,353 سطرًا، **42 خاصية `@Published`**، و~20 تبعية
خدمة خاصة. يملك: التنقّل، الإعدادات، المواقع، أوقات التشغيل، محرك البيئة،
البريد، الـ dumps، DNS، الشهادات، الخدمات، المُنقّح، التحديثات، الإعداد الأول،
الطرفية، ومحرر الأكواد، و`NSOpenPanel`.

وهو مُمرَّر لكل واجهة عبر `@EnvironmentObject` واحد. النتيجة: أي تغيير في أي
خاصية — مثل وصول رسالة بريد (`AppModel.swift:1036`) أو تحديث حالة خدمة
(`AppModel.swift:815`) — يُبطِل **كل** الواجهات المشتركة، بما فيها الصفحات غير
النشطة.

**التفكيك المقترح**:

| النوع الجديد | المسؤوليات |
|--------------|-----------|
| `AppNavigation` | `selectedPage`، توجيه النوافذ |
| `SitesCoordinator` | `sites`، `selectedSiteID`، مسارات park، الربط، PHP/Node لكل موقع |
| `EnvironmentCoordinator` | `environmentStatus`، المنافذ، التشغيل/الإيقاف، كشف التعارض |
| `RuntimeCoordinator` | إصدارات PHP/Node، التثبيت، Composer، مثبّت Laravel |
| `MailCoordinator` / `DumpsCoordinator` | الرسائل والـ dumps ودورة حياة الخوادم |
| `ServicesCoordinator` | `serviceInstances`، `serviceStates` |
| `SecuritySetupCoordinator` | DNS، الشهادات، مُحلِّل النطاقات، الإعداد الأول |

مع الانتقال إلى `@Observable` (macOS 14+) و`@ObservationIgnored` للخدمات، يصبح
الإبطال على مستوى الخاصية المقروءة فعليًا بدل الكائن كله.

#### [عالية] F-2: عمل حاجز على الـ main thread

| الموقع | المشكلة |
|--------|---------|
| `AppModel.swift:187-197` | `refresh()` يُجري فحص نظام ملفات **متزامنًا** على `@MainActor` — يُنادى من `init` (سطر 110) ومن `SitesView.swift:35` |
| `AppModel.swift:1121-1148` | `detectEnvironmentStatus()` يُشغّل `lsof` متزامنًا بـ `process.waitUntilExit()` |
| `AppModel.swift:1322-1350` | الإعداد الأول يستقصي حالة المُحلِّل/الشهادة كل 500ms × 120 على الـ main actor |

**الإصلاح**: نقل الفحص واستقصاء العمليات إلى `Task.detached`/actor، والنشر على
`MainActor` فقط، مع ربط `isRefreshing` بحالة تحميل مرئية (هي موجودة في
`AppModel.swift:15` لكنها **لا تُستخدم في أي واجهة**).

#### [عالية] F-3: منطق واجهة داخل الموديل

`AppModel.swift:346-361` — `addParkPath()` يعرض `NSOpenPanel.runModal()` داخل
الموديل: يحجب الـ main thread، غير قابل للاختبار، ويخلط الطبقات. نفس النمط في
`CreateSiteWizardView.swift:326-340`.

**الإصلاح**: عرض اللوحة في الواجهة، والموديل يستقبل `URL` فقط.

#### [متوسطة] F-4: تجاوز حقن التبعيات في `LogsView`

`LogsView.swift:15-17` — `store` **خاصية محسوبة** تُنشئ `LogStore` جديدًا في كل
قراءة، بينما `AppModel` يملك `logStore` خاصًا (`AppModel.swift:68`).

```swift
private var store: LogStore {
    LogStore(rootURL: model.configurationStore.rootURL.appendingPathComponent("Log"))
}
```

**الإصلاح**: تمرير `LogStore` عبر البيئة أو `AppModel`.

---

### 3.2 أداء SwiftUI

#### [عالية] F-5: `LogsView` أسوأ نقطة أداء في التطبيق

`LogsView.swift:13,41-43` — مؤقت كل ثانية على `.main`:

```swift
private let refreshTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()
...
.onReceive(refreshTimer) { _ in
    if autoScroll { reloadFiles() }
}
```

كل تكة: إنشاء `LogStore` جديد (F-4) + فحص المجلد + **قراءة ملف السجل كاملًا**
إلى `@State content`. ثم `displayedContent` (سطر 23-28) يُقسّم ويُرشّح النص
الكامل في كل تكة وكل ضغطة مفتاح — O(n) لكل تحديث.

**الإصلاح**: تتبّع الملف عبر `DispatchSourceFileSystemObject`/FSEvents مع إضافة
الفروق فقط، فهرسة بالأسطر للبحث، `LazyVStack` مع نطاق مرئي، وتأخير البحث 200ms.

#### قوائم غير كسولة (Medium)

| الملف | الأسطر | المشكلة |
|-------|--------|---------|
| `ServicesView.swift` | 34-92 | صفوف الخدمات في `VStack` + `ForEach` — كلها تُخطَّط دفعة واحدة |
| `PHPView.swift` | 22-52 | نفس النمط داخل `ScrollView` |
| `NodeView.swift` | 22-55 | نفس النمط |
| `DumpsView.swift` | 9-11 | `.reversed()` يُخصّص مصفوفة جديدة في كل تصيير |
| `SitesView.swift` | 106-124 | `List` بأزرار بدل `List(selection:)` — تمييز فروق أقل كفاءة |
| `SitesView.swift` | 303-385 | `WKWebView` كامل (1440×934) لكل موقع مُحدَّد، يُعاد تحميله عند تغيّر الرابط |

**الإصلاح**: `List` أو `LazyVStack`، استخراج الصفوف كأنواع `Equatable`، تحميل
كسول للمعاينة وإيقافها عند `showPreview == false`.

#### [متوسطة] F-6: صفر إلغاء للمهام

لا يوجد أي استخدام لـ `.task` أو `.task(id:)` في المشروع. العمليات الطويلة
(`CreateSiteWizardView.swift:367-380`، كتل `Task` في `AppModel`) تستمر بعد إغلاق
النافذة.

**الإصلاح**: `.task(id:)` للعمل القابل لإعادة التحميل، وحفظ مقابض `Task`
وإلغاؤها في `.onDisappear`.

#### [منخفضة] F-7: تخطيط غير مستقر

`PHPView.swift:51` و`NodeView.swift:54` يستخدمان
`runtime.cycle.hashValue.isMultiple(of: 2)` لتلوين الصفوف — و`hashValue` يتغيّر
بين كل تشغيل وآخر في Swift.

**الإصلاح**: `enumerated().offset`.

---

### 3.3 تجربة المستخدم

#### [عالية] الفجوات الأكبر مقارنة بتوقعات مطوّر Laravel

| المعرّف | الفجوة | الموقع | الإصلاح |
|---------|--------|--------|---------|
| F-8 | لا حالة لكل موقع في القائمة — نص النطاق فقط، و`siteRuntimePorts` (`AppModel.swift:17`) غير مستخدم في الواجهة | `SitesView.swift:106-122` | نقطة حالة (يعمل/متوقف/خطأ) + شعار إصدار PHP + أيقونة الإطار |
| F-9 | "Logs" في قائمة الموقع تفتح سجلات HerdMe العامة بدون سياق الموقع | `SitesView.swift:212` | تمرير `selectedSiteID` وكشف `storage/logs/laravel.log` |
| F-10 | لا تتبّع لسجل Laravel إطلاقًا | `LogsView.swift` | منتقي سجلات على مستوى الموقع + وضع متابعة (follow) |
| F-11 | لا لوحة أوامر ولا اختصارات (فقط `⌘,`) | `HerdMeApp.swift:48-56` | `CommandMenu` + لوحة `⌘K`: المواقع، تشغيل/إيقاف البيئة، إنشاء موقع، فتح الطرفية |
| F-12 | الأخطاء تظهر كتنبيهات حاجزة فقط | `RootView.swift:23-30` | إشعارات غير حاجزة (toast) — `ErrorPresentation` موجود ومُستخدم في المعالج فقط |
| F-13 | لا بحث في البريد | `MailView.swift:82-107` | حقل بحث بالمرسل/الموضوع/التاريخ |
| F-14 | قائمة مسارات park بارتفاع 78pt فقط | `GeneralView.swift:19-27` | ارتفاع تلقائي أو 150pt كحدّ أدنى + عدد المواقع المكتشفة لكل مسار |

#### [متوسطة] فجوات إضافية

- **لا سحب وإفلات** للمجلدات على قائمة park (`GeneralView.swift:19-53`).
- **لا نسخ للحافظة** للنطاق/الرابط/المسار (`SitesView.swift:274`) — النمط موجود
  في `MailView.swift:50`.
- **لا قائمة سياق** بالزر الأيمن على صفوف المواقع.
- **لا تجميع للخدمات** حسب `ServiceCategory` (موجود في `AppModels.swift:121-127`
  وغير مستخدم).
- **لا هياكل تحميل** (skeletons) أثناء التحديث/التثبيت.
- **حالات فراغ فارغة**: `MailView.swift:83,158` و`DumpsView.swift:60,92` تُمرِّر
  `message: ""` — فرصة مهدرة لشرح `MAIL_MAILER=smtp` و`dump()`.
- **حقل TLD يفقد التعديلات** عند الانتقال (`GeneralView.swift:60-63` يستخدم
  `@State` محليًا ولا يحفظ إلا عند Submit).
- **شريط القوائم فقير**: `MenuBarContentView.swift:8-47` بلا مواقع حديثة ولا عدّاد
  بريد ولا مؤشر حالة البيئة.
- **حجم النافذة**: العرض يتغيّر حسب الصفحة (730/900) لكن الارتفاع ثابت 527 بلا
  استعادة للإطار (`RootView.swift:67-86`)، والتنفيذ الحالي بـ
  `DispatchQueue.main.async` + `setContentSize` قد يتعارض مع تحجيم المستخدم.

---

### 3.4 الوصولية (Accessibility)

**النتيجة الحالية: شبه معدومة.** الاستخدام الوحيد للوصولية في المشروع كله هو
`CreateSiteWizardView.swift:280` (`.accessibilityElement(children: .combine)`).
صفر مطابقات لـ `accessibilityLabel` أو `accessibilityHint` أو `dynamicTypeSize`.

| المعرّف | المشكلة | الإصلاح |
|---------|---------|---------|
| F-15 | أزرار شريط الأدوات أيقونات فقط مع `.help()` — و**tooltips ليست VoiceOver** | `.accessibilityLabel` لكل زر ("تحديث المواقع"، "تشغيل البيئة") |
| F-16 | الحالة تُنقل بدائرة ملوّنة فقط (`SitesView.swift:45-54`, `AppModels.swift:348`) | `.accessibilityValue` نصية + تسمية نصية مرافقة للون دائمًا |
| F-17 | التنقّل الجانبي بلا إعلان للتحديد | `.accessibilityAddTraits(.isSelected)` |
| F-18 | خطوط ثابتة `.system(size: 24/14/12)` (`Components.swift:34,61`) | `.font(.title)` أو `@ScaledMetric` |
| F-19 | لا ترتيب تركيز في المعالج | `@FocusState` على الحقل الرئيسي + زر افتراضي عند Return |

---

### 3.5 التعريب و RTL

#### [عالية] F-20: لا يوجد تعريب مطلقًا

تحققت: **صفر** مطابقات لـ `Localizable` أو `LocalizedStringKey` أو
`NSLocalizedString` أو `.xcstrings` في المشروع بأكمله. كل النصوص مكتوبة
بالإنجليزية داخل الكود، بما فيها رسائل الأخطاء (`ErrorPresentation.swift:7-42`)
ومراحل الإعداد (`AppModels.swift:306-338`).

بالنسبة لمشروع مالكه عربي، هذه أولوية عالية.

**الإصلاح**:
1. إضافة String Catalog (`.xcstrings`) وتغليف كل النصوص المرئية.
2. إضافة لغة `ar` مع دعم `layoutDirection` واختبار RTL على الشريط الجانبي
   والمعالج ومعاينة البريد.
3. إصلاح تسريب لغوي في البيانات: `Components.swift:112-119` يخزّن قيم السمة
   كنصوص إنجليزية (`"Auto"`, `"Light"`, `"Dark"`) في الإعدادات — يجب تخزين قيم
   `enum` خام (`auto`/`light`/`dark`) وتعريب العرض فقط.
4. تنسيق التواريخ والأرقام حسب `Locale` (`MailView.swift:94` يعرض الوقت فقط بلا
   تاريخ للرسائل القديمة).

---

### 3.6 التصميم والصقل

| المعرّف | المشكلة | الموقع | الإصلاح |
|---------|---------|--------|---------|
| F-21 | تسلسل معلوماتي ضعيف في تفاصيل الموقع: معاينة 162×105 صغيرة، النطاق مكرر، الإطار مخفي في تبويب | `SitesView.swift:182-283` | رأس بارز: الاسم، النطاق، شعار الإطار، شرائح PHP/Node، أزرار Open/Tinker/Terminal |
| F-22 | ترويسات أعمدة الخدمات تطفو فوق صفوف غير مُحاذاة | `ServicesView.swift:24-31` | `Grid` أو `Table` مع أعمدة قابلة للترتيب |
| F-23 | المعالج يستخدم `step: Int` مبهمًا بلا مؤشر خطوات | `CreateSiteWizardView.swift:16,28-43` | مؤشر أفقي: Template → Starter → Configure |
| F-24 | عند فشل الإعداد الأول: "Try Again" فقط بلا تفاصيل تقنية | `OnboardingView.swift:83-98` | إعادة استخدام `ErrorPresentation.technicalDetails` (النمط موجود في `CreateSiteWizardView.swift:221`) |
| F-25 | تسمية "Start On Trigger" مربوطة بـ `detectBreakpoints` — مضلّلة | `DebuggerView.swift:46-52` | مواءمة المصطلحات مع توثيق Xdebug |
| F-26 | صفحة "حول" فقيرة: بلا روابط GitHub/توثيق/ملاحظات إصدار | `AboutView.swift:21-54` | روابط + "نسخ الإصدار" + تكامل التحقق من التحديثات |
| F-27 | "Active" مخفية بـ `opacity(0)` وتحتلّ مساحة | `NodeView.swift:34-36` | `if runtime.isActive` |
| F-28 | أيقونة شريط القوائم `h.square.fill` عامة بلا مؤشر حالة | `MenuBarContentView.swift:70` | صورة قالب + نقطة تلوين خضراء/حمراء |

---

## 4. الإضافات المقترحة

هذه ميزات **غير موجودة حاليًا** (لا حتى كهياكل فارغة)، مرتّبة حسب نسبة
القيمة إلى الجهد.

### 4.1 قيمة عالية / جهد منخفض

| # | الإضافة | الوصف |
|---|---------|-------|
| A-1 | **مُشغّل Artisan** | حاليًا Tinker يفتح طرفية خارجية فقط (`AppModel.swift:707-724`). الإضافة: واجهة لتشغيل `migrate` و`route:list` و`queue:work` وأوامر مخصّصة مع عرض المخرجات مباشرة داخل التطبيق |
| A-2 | **محرر `.env`** | حاليًا يوجد فقط "نسخ إعدادات البريد" في `MailView`. الإضافة: عارض/محرر `.env` لكل موقع مع تعبئة تلقائية لبيانات اتصال الخدمات المُدارة العاملة |
| A-3 | **تتبّع سجل Laravel لكل موقع** | أكبر فجوة مقابل Herd/Valet — انظر F-9 و F-10 |
| A-4 | **لوحة أوامر `⌘K`** | انظر F-11 |
| A-5 | **نسخ روابط الاتصال** | `mysql://`, `redis://`, `postgresql://` لكل خدمة عاملة. المنطق موجود في `TablePlusConnection.swift` ويحتاج فقط كشفه في الواجهة + نقله إلى Windows (B-42) |
| A-6 | **مُشغّل سكربتات npm** | إصدارات Node مُدارة بالفعل؛ الإضافة: `npm run dev` / `build` من الواجهة مع اكتشاف السكربتات من `package.json` |

### 4.2 قيمة عالية / جهد متوسط

| # | الإضافة | الوصف |
|---|---------|-------|
| A-7 | **لوحة معلومات (Dashboard)** | صفحة رئيسية: عدد المواقع العاملة، صحة الخدمات، آخر رسائل البريد والـ dumps، حالة البيئة، تحذيرات (منفذ مشغول، شهادة قاربت الانتهاء) |
| A-8 | **صفحة تفاصيل كاملة للموقع** | التبويبات الحالية (General/Information) ناقصة: أضف السجلات، `.env`، المسارات (routes)، الخدمات المرتبطة، حالة Git، إصدارات PHP/Node |
| A-9 | **إجراءات جماعية** | تحديد متعدد: تشغيل/إيقاف، تغيير إصدار PHP لمجموعة مواقع |
| A-10 | **لوحة حالة Git** | الفرع الحالي وحالة النظافة في قائمة المواقع والتفاصيل |
| A-11 | **مراقب الطوابير / Horizon** | Redis قابل للتثبيت بالفعل؛ الإضافة: رابط لوحة Horizon، حالة عمّال الطوابير، إعادة تشغيلهم |
| A-12 | **REPL Tinker مدمج** | مستخدمو Herd يتوقّعون REPL داخل التطبيق لا طرفية خارجية |
| A-13 | **عارض قواعد البيانات** | تكامل TablePlus موجود للخدمات (`AppModel.swift:907-929`) لكن لا يوجد متصفح جداول مدمج ولا استعلامات سريعة |

### 4.3 قيمة متوسطة

| # | الإضافة | الوصف |
|---|---------|-------|
| A-14 | **تصدير/استيراد الإعدادات** | حاليًا `configurationStore` محلي فقط؛ الإضافة: ملفات تعريف (profiles) للفرق |
| A-15 | **لوحة أداء/تشخيص** | تفعيل تحليل Xdebug (profiling)، رابط Laravel Telescope، توقيت الطلبات |
| A-16 | **إشعارات نظام** | عند فشل بدء خدمة، أو وصول بريد، أو انتهاء إنشاء مشروع |
| A-17 | **قوالب مشاريع** | ما بعد starter kits: قوالب محلية مخصّصة للفريق |
| A-18 | **دعم Docker/Sail اختياريًا** | كشف `docker-compose.yml` وعرض حالة الحاويات |
| A-19 | **معالجة الصور / تحسين الأصول** | خدمات مساعدة شائعة في بيئات التطوير المحلية |

---

## 5. البنية التحتية والإصدار

### 5.1 CI — الفجوة الأكبر

`.github/workflows/windows-x64.yml` (48 سطرًا) هو **مهمة CI الوحيدة** في
المشروع، وتشغّل `acceptance.ps1` على `windows-2022`.

| المعرّف | الفجوة | الخطورة |
|---------|--------|---------|
| I-1 | **لا مهمة macOS إطلاقًا** — 78 اختبار XCTest لا تعمل آليًا | حرجة |
| I-2 | لا بناء/اختبار للنواة C++ في CI (خارج build.ps1) | عالية |
| I-3 | اختبارات Windows تصيب عناوين upstream حقيقية في كل PR (استقصاءات `--live-*`) | عالية |
| I-4 | لا CodeQL ولا تحليل ساكن | عالية |
| I-5 | لا Dependabot/Renovate لـ NuGet وGitHub Actions | متوسطة |
| I-6 | لا بوابات تنسيق/lint (SwiftLint، clang-format، dotnet format) | متوسطة |
| I-7 | Release فقط — لا مصفوفة Debug/Release | متوسطة |
| I-8 | لا توقيع للمخرجات في CI | متوسطة |

**الإصلاح المقترح**:

```yaml
# مهمة macOS مطلوبة
runs-on: macos-15
steps:
  - xcodegen generate
  - xcodebuild -scheme HerdMe -configuration Debug test
  - cmake -S Core -B build/core && ctest --test-dir build/core
  - ./scripts/package-macos.sh Release
```

مع فصل استقصاءات `--live-*` إلى workflow مُجدول (nightly)، وإبقاء CI الخاص
بالـ PR بلا شبكة عبر تجهيزات ثابتة (fixtures).

### 5.2 الاختبارات

| المعرّف | الفجوة | التفصيل |
|---------|--------|---------|
| I-9 | لا تكامل بين macOS والنواة | macOS لا يستدعي `herdme-core` أبدًا → التعاقد المشترك غير مُختبر على المنصة الأساسية |
| I-10 | تغطية النواة ضعيفة | `Core/tests/core_tests.cpp` = 112 سطرًا / 6 سيناريوهات. **لا اختبارات لـ `dns_label()`** رغم وجودها في Swift، ولا لكشف الأُطر، ولا لمخرجات JSON، ولا لـ `inspect_runtimes()` |
| I-11 | `CoreClient` غير مُختبر مباشرة | يُترجم في التعاقدات لكن لا يوجد استدعاء لـ `ScanAsync`/`DoctorAsync`/`ValidatePhpAsync` → التعاقد عبر العملية الفرعية غير مُتحقَّق منه |
| I-12 | لا اختبارات واجهة | README يذكر "فحوص بصرية أصلية" لكنها يدوية بالكامل — لا XCUITest ولا snapshot ولا WinAppDriver |
| I-13 | لا قياس تغطية | لا `llvm-cov` ولا Codecov |
| I-14 | لا fuzzing لمُحلّلات الإدخال غير الموثوق | MIME وDNS وFastCGI وPHP serialization — كلها مرشّحات ممتازة لـ libFuzzer |
| I-15 | ملفات اختبار عملاقة | 2,064 سطرًا في صنف Swift واحد، و1,311 سطرًا في `Program.cs` بلا xUnit |
| I-16 | البوابة الحيّة لـ Laravel مُتخطّاة افتراضيًا | `testExistingLaravelProjectThroughFPMWhenRequested` يحتاج متغير بيئة |

### 5.3 التوقيع والتوزيع

| المعرّف | الفجوة | الموقع |
|---------|--------|--------|
| I-17 | `CODE_SIGN_IDENTITY: "-"` و`ENABLE_HARDENED_RUNTIME: NO` | `project.yml:64-65` |
| I-18 | لا notarization | `scripts/package-macos.sh:73-78` — ZIP وDMG بتوقيع ad-hoc فقط |
| I-19 | لا Authenticode على Windows | ZIP له بصمة `.sha256` لكن التنفيذيات غير موقّعة |
| I-20 | لا MSIX ولا مُثبِّت | `HerdMe.Windows.csproj:11` — `WindowsPackageType=None` |
| I-21 | x64 فقط — لا ARM64 على Windows | `csproj:8-9`، `build.ps1:10-11` |
| I-22 | `downloadURL: null` في بيان الإصدار | التحقق من التحديثات يعمل لكن التسليم لا |
| I-23 | لا WinGet/Chocolatey/Scoop ولا Homebrew cask | — |
| I-24 | تبعية WebView2 غير معلنة | `SitesPage.xaml:172` و`MailPage.xaml:92` يستخدمان WebView2 بلا مرجع حزمة صريح في csproj |
| I-25 | لا CHANGELOG ولا مخطط إصدارات موحّد | `0.1.0` مكرّر في 4 مواضع أو أكثر |
| I-26 | لا SBOM ولا بناء قابل للتكرار | Homebrew غير مثبّت الإصدارات |

### 5.4 نظافة المستودع والتوثيق

**ملفات مفقودة**:

| الملف | الخطورة | السبب |
|-------|---------|-------|
| `SECURITY.md` | **عالية** | التطبيق يُنزّل وينفّذ ثنائيات من أطراف ثالثة ويطلب صلاحيات مرتفعة — سياسة إبلاغ الثغرات ضرورية |
| `CONTRIBUTING.md` | متوسطة | — |
| `.editorconfig`, `.clang-format`, `.swiftlint.yml` | متوسطة | — |
| `CODE_OF_CONDUCT.md`, قوالب Issues/PR | منخفضة | — |

**دقة التوثيق** — عبارات تحتاج تصحيحًا:

| الموضع | الادعاء | الواقع |
|--------|---------|--------|
| `README.md:8-10` | "النواة المشتركة تُشغّل تطبيقي macOS وWindows" | macOS لا يبنيها ولا يستدعيها |
| `docs/PARITY.md:14-26` | "نفس نواة C++20 وتعاقدات JSON" للفحص والنطاقات والخدمات | جزئي جدًا؛ الباقي مكرّر |
| `docs/PARITY.md:45` | "كتلة hosts معزولة" مكافئة لـ DNS على macOS | مبالغة — انظر B-40 |
| `README.md:118-119` | "اختبارات C++ المحمولة تنجح على macOS" | صحيح محليًا، **ليس في CI** |
| `README.md:124-125` | "فحوص بصرية أصلية" | يدوية بالكامل |
| `docs/THIRD_PARTY.md` | دقيق للتراخيص | **لا يوثّق سلسلة توريد Homebrew على macOS** مقابل نموذج البصمات على Windows |

### 5.5 سلسلة توريد الطرف الثالث

Windows نموذجه قوي: SHA-256 لكل شيء (PHP، Node، Composer، Xdebug، كل الخدمات)،
مع استثناءين مُوثَّقين بأمانة (MySQL بـ MD5 من كشط HTML، PostgreSQL ببصمة مثبّتة).

macOS نموذجه مختلف تمامًا وغير موثّق:

| المكوّن | المصدر | التحقق |
|---------|--------|--------|
| PHP | Homebrew (`shivammathur/php`) | قائمة سماح للصيغ، **لا بصمة** |
| Node | تنزيل مباشر | **لا شيء** (B-1) |
| Composer | تنزيل مباشر | SHA-384 ✅ |
| الخدمات | صيغ Homebrew | تحليل `brew info`، لا بصمة للثنائي |
| Xdebug | PECL | **لا شيء** (B-2) |

**الإصلاح**: إضافة قسم macOS إلى `docs/THIRD_PARTY.md` يشرح نموذج ثقة Homebrew
وتثبيت الصيغ، وإصلاح B-1 وB-2 لمواءمة مستوى الأمان مع Windows.

---

## 6. خطة التنفيذ المقترحة

### المرحلة 0 — إصلاحات أمان وموثوقية عاجلة (أسبوع 1)

هذه لا تحتمل التأخير لأنها تمسّ سلامة المستخدم أو تُسبب تجميدًا:

1. **B-1**: تحقق SHA-256 لتنزيلات Node على macOS.
2. **B-11**: نوع `ProcessRunner` مشترك يقرأ الأنابيب بشكل غير متزامن مع مهلة
   وإلغاء — ثم ترحيل كل الاستدعاءات العشرة إليه.
3. **B-5**: فرض حدّ 50MB في SMTP على macOS.
4. **B-12**: إصلاح تسريب الجلسات في SMTP والـ dumps.
5. **B-6**: سقوف لمُحلّل PHP serialization و`DumpCaptureServer`.
6. **I-1**: مهمة CI لـ macOS تشغّل الاختبارات الـ78 + CTest للنواة.
7. **SECURITY.md**.

### المرحلة 1 — الأساسات (أسابيع 2-4)

8. **B-29**: قرار صريح بشأن النواة، ثم إما التبنّي على macOS أو تصحيح التوثيق —
   بالإضافة إلى **توحيد تحليل `php -m`** فورًا لأن التباعد الدلالي موجود اليوم.
9. **F-1**: تفكيك `AppModel` إلى المنسّقين المقترحين والانتقال إلى `@Observable`.
10. **F-2**: إخراج الفحص و`lsof` والاستقصاء من الـ main thread.
11. **F-5**: إعادة كتابة `LogsView` (تتبّع الملف، فهرسة، تحميل كسول).
12. **B-2** و**B-3**: تحقق Xdebug وتوقيع التحديثات.
13. **B-39**: بوابة 80/443 على Windows.
14. **B-30**: بروتوكولات وحقن تبعيات على macOS، وحاوية DI على Windows.

### المرحلة 2 — التجربة والتكافؤ (أسابيع 5-8)

15. **F-20**: String Catalog + لغة `ar` + اختبار RTL.
16. **A-3/F-9/F-10**: سجلات Laravel لكل موقع مع وضع متابعة.
17. **F-8**: مؤشرات حالة لكل موقع في القائمة.
18. **A-1** و**A-2**: مُشغّل Artisan ومحرر `.env`.
19. **A-4/F-11**: لوحة أوامر واختصارات لوحة مفاتيح.
20. **F-12**: إشعارات أخطاء غير حاجزة.
21. **F-15..F-19**: جولة وصولية على شريط الأدوات والشريط الجانبي والخدمات.
22. **B-22** و**B-23**: استعادة الخدمات ومراقبة صحتها على Windows.
23. **B-42/A-5**: روابط اتصال قواعد البيانات على المنصتين.

### المرحلة 3 — التوزيع والنمو (أسابيع 9-12)

24. **I-17..I-19**: Developer ID + notarization على macOS، وAuthenticode على Windows.
25. **I-20**: MSIX/مُثبِّت لـ Windows.
26. **I-22**: تفعيل تسليم التحديثات فعليًا (Sparkle أو تنزيل داخلي موقّع).
27. **A-7** و**A-8**: لوحة المعلومات وصفحة تفاصيل الموقع الكاملة.
28. **B-33..B-36**: بثّ FastCGI والملفات الساكنة + keep-alive.
29. **I-4** و**I-5** و**I-6**: CodeQL وDependabot وبوابات lint.
30. **I-14**: أهداف fuzzing لمُحلّلات MIME وDNS وFastCGI.
31. **I-23**: Homebrew cask وWinGet بعد توفّر التوقيع.

---

## ملحق: مصفوفة الأولويات السريعة

| الأثر ↓ / الجهد → | منخفض | متوسط | عالي |
|---|---|---|---|
| **عالي** | B-1, B-5, B-12, B-6, SECURITY.md, F-7, F-27 | B-11, I-1, F-5, F-2, B-2, B-3, B-39, F-8 | B-29, F-1, F-20, B-30, I-17..I-20 |
| **متوسط** | F-13, F-14, A-5, F-24, F-26, F-28 | A-1, A-2, A-4, F-12, B-22, B-18 | A-7, A-8, A-13, B-33..B-36 |
| **منخفض** | F-6, B-46, B-16 | A-9, A-10, A-16, I-6 | A-18, A-19, I-14, I-26 |
