import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

const supportedAppLocales = [
  Locale('zh', 'CN'),
  Locale('en', 'US'),
  Locale('ja', 'JP'),
];

Locale appLocale(Object? value) => switch (value) {
  'en-US' => supportedAppLocales[1],
  'ja-JP' => supportedAppLocales[2],
  _ => supportedAppLocales[0],
};

String appLocaleName(Locale locale) =>
    '${locale.languageCode}-${locale.countryCode}';

class AppLocalizations {
  const AppLocalizations(this.values);

  final Map<String, dynamic> values;

  static AppLocalizations of(BuildContext context) =>
      Localizations.of<AppLocalizations>(context, AppLocalizations) ??
      _fallbackLocalizations;

  String text(String key, [Map<String, Object> parameters = const {}]) {
    Object? value = values;
    for (final part in key.split('.')) {
      if (value is! Map || !value.containsKey(part)) return key;
      value = value[part];
    }
    var result = value is String ? value : key;
    for (final entry in parameters.entries) {
      result = result.replaceAll('{{${entry.key}}}', entry.value.toString());
    }
    return result;
  }
}

const _fallbackLocalizations = AppLocalizations({
  'appShell': {
    'tabs': {'status': '状态', 'curve': '曲线', 'control': '设置', 'about': '关于'},
  },
  'fanCurve': {'title': '风扇曲线'},
  'aboutPanel': {'title': '关于 {{name}}'},
  'common': {
    'language': '语言',
    'languages': {'zh-CN': '简体中文', 'en-US': '英语', 'ja-JP': '日语'},
  },
});

class AppLocalizationsDelegate extends LocalizationsDelegate<AppLocalizations> {
  const AppLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) => supportedAppLocales.any(
    (item) => item.languageCode == locale.languageCode,
  );

  @override
  Future<AppLocalizations> load(Locale locale) async {
    final name = appLocaleName(appLocale(appLocaleName(locale)));
    final decoded = jsonDecode(
      await rootBundle.loadString('assets/locales/$name.json'),
    );
    return AppLocalizations(Map<String, dynamic>.from(decoded as Map));
  }

  @override
  bool shouldReload(AppLocalizationsDelegate old) => false;
}
