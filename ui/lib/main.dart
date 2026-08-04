import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'app_controller.dart';
import 'smooth_scroll.dart';

void main() => runApp(const ThrmApp());

class ThrmApp extends StatefulWidget {
  const ThrmApp({super.key});

  @override
  State<ThrmApp> createState() => _ThrmAppState();
}

class _ThrmAppState extends State<ThrmApp> {
  late final AppController controller;

  @override
  void initState() {
    super.initState();
    controller = AppController()..start();
  }

  @override
  void dispose() {
    controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'THRM',
      debugShowCheckedModeBanner: false,
      locale: const Locale('zh', 'CN'),
      localizationsDelegates: GlobalMaterialLocalizations.delegates,
      supportedLocales: const [Locale('zh', 'CN')],
      themeMode: ThemeMode.system,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xff6750a4)),
        useMaterial3: true,
      ),
      darkTheme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xffb69df8),
          brightness: Brightness.dark,
        ),
        useMaterial3: true,
      ),
      home: ThrmShell(controller: controller),
    );
  }
}

enum ThrmPage { status, curve, control, about }

const _pageLabels = ['状态', '曲线', '控制', '关于'];
const _pageIcons = [
  Icons.dashboard_outlined,
  Icons.show_chart,
  Icons.tune,
  Icons.info_outline,
];
const _selectedPageIcons = [
  Icons.dashboard,
  Icons.show_chart,
  Icons.tune,
  Icons.info,
];

class ThrmShell extends StatefulWidget {
  const ThrmShell({required this.controller, super.key});

  final AppController controller;

  @override
  State<ThrmShell> createState() => _ThrmShellState();
}

class _ThrmShellState extends State<ThrmShell> {
  ThrmPage page = ThrmPage.status;
  final statusScrollController = SmoothScrollController();
  final aboutScrollController = SmoothScrollController();

  @override
  void dispose() {
    statusScrollController.dispose();
    aboutScrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final selected = page.index;
    return Scaffold(
      appBar: AppBar(
        title: Text('THRM · ${_pageLabels[selected]}'),
        actions: [
          AnimatedBuilder(
            animation: widget.controller,
            builder: (_, _) {
              final online =
                  widget.controller.connection == CoreConnection.connected;
              return Padding(
                padding: const EdgeInsets.only(right: 16),
                child: Chip(
                  avatar: Icon(online ? Icons.link : Icons.link_off, size: 18),
                  label: Text(online ? 'Core 在线' : 'Core 离线'),
                ),
              );
            },
          ),
        ],
      ),
      body: LayoutBuilder(
        builder: (context, constraints) {
          final content = _page(page);
          if (constraints.maxWidth < 820) return content;
          return Row(
            children: [
              NavigationRail(
                selectedIndex: selected,
                labelType: NavigationRailLabelType.all,
                onDestinationSelected: _selectPage,
                destinations: List.generate(
                  ThrmPage.values.length,
                  (index) => NavigationRailDestination(
                    icon: Icon(_pageIcons[index]),
                    selectedIcon: Icon(_selectedPageIcons[index]),
                    label: Text(_pageLabels[index]),
                  ),
                ),
              ),
              const VerticalDivider(width: 1),
              Expanded(child: content),
            ],
          );
        },
      ),
      bottomNavigationBar: MediaQuery.sizeOf(context).width < 820
          ? NavigationBar(
              selectedIndex: selected,
              onDestinationSelected: _selectPage,
              destinations: List.generate(
                ThrmPage.values.length,
                (index) => NavigationDestination(
                  icon: Icon(_pageIcons[index]),
                  selectedIcon: Icon(_selectedPageIcons[index]),
                  label: _pageLabels[index],
                ),
              ),
            )
          : null,
    );
  }

  void _selectPage(int index) => setState(() => page = ThrmPage.values[index]);

  Widget _page(ThrmPage selected) => switch (selected) {
    ThrmPage.status => StatusPage(
      controller: widget.controller,
      scrollController: statusScrollController,
    ),
    ThrmPage.curve => const PlaceholderPage(
      icon: Icons.show_chart,
      title: '曲线页',
      message: '状态页稳定后迁移曲线与历史。',
    ),
    ThrmPage.control => const PlaceholderPage(
      icon: Icons.tune,
      title: '控制页',
      message: '硬件控制仍由现有 Go Core 负责。',
    ),
    ThrmPage.about => RenderingLabPage(scrollController: aboutScrollController),
  };
}

class StatusPage extends StatelessWidget {
  const StatusPage({
    required this.controller,
    required this.scrollController,
    super.key,
  });

  final AppController controller;
  final ScrollController scrollController;

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: controller,
      builder: (context, _) {
        final temp = controller.temperature;
        final fan = controller.fanData;
        final status = controller.deviceStatus;
        final connected = controller.connection == CoreConnection.connected;
        return RefreshIndicator(
          onRefresh: controller.refresh,
          child: ListView(
            controller: scrollController,
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.all(20),
            children: [
              Card(
                color: connected
                    ? Theme.of(context).colorScheme.primaryContainer
                    : Theme.of(context).colorScheme.errorContainer,
                child: ListTile(
                  leading: Icon(
                    connected ? Icons.cloud_done : Icons.cloud_off,
                    size: 32,
                  ),
                  title: Text(connected ? '已连接 THRM Core' : '正在等待 THRM Core'),
                  subtitle: Text(
                    controller.error ??
                        (controller.deviceConnected
                            ? '设备已连接，实时事件由 Core 推送'
                            : 'Core 在线，设备当前未连接'),
                  ),
                  trailing: IconButton(
                    tooltip: '刷新快照',
                    onPressed: connected ? controller.refresh : null,
                    icon: const Icon(Icons.refresh),
                  ),
                ),
              ),
              const SizedBox(height: 16),
              Text('实时状态', style: Theme.of(context).textTheme.titleLarge),
              const SizedBox(height: 8),
              GridView.extent(
                maxCrossAxisExtent: 280,
                shrinkWrap: true,
                physics: const NeverScrollableScrollPhysics(),
                childAspectRatio: 1.55,
                mainAxisSpacing: 8,
                crossAxisSpacing: 8,
                children: [
                  MetricCard(
                    icon: Icons.memory,
                    label: 'CPU 温度',
                    value: _metric(temp, 'cpuTemp', '°C'),
                  ),
                  MetricCard(
                    icon: Icons.videogame_asset_outlined,
                    label: 'GPU 温度',
                    value: _metric(temp, 'gpuTemp', '°C'),
                  ),
                  MetricCard(
                    icon: Icons.air,
                    label: '当前转速',
                    value: _metric(fan, 'currentRpm', ' RPM'),
                  ),
                  MetricCard(
                    icon: Icons.speed,
                    label: '目标转速',
                    value: _metric(fan, 'targetRpm', ' RPM'),
                  ),
                ],
              ),
              const SizedBox(height: 16),
              Card(
                child: Column(
                  children: [
                    ListTile(
                      leading: Icon(
                        controller.deviceConnected ? Icons.usb : Icons.usb_off,
                      ),
                      title: Text(
                        controller.deviceConnected ? '设备已连接' : '设备未连接',
                      ),
                      subtitle: Text(
                        '${status?['model'] ?? '未知型号'} · '
                        '${status?['productId'] ?? '--'}',
                      ),
                    ),
                    const Divider(height: 1),
                    ListTile(
                      leading: const Icon(Icons.auto_awesome),
                      title: const Text('控制模式'),
                      subtitle: Text(
                        controller.config?['autoControl'] == true
                            ? '智能控温'
                            : '手动模式',
                      ),
                      trailing: Text('事件：${controller.lastEvent ?? '--'}'),
                    ),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  String _metric(Map<String, dynamic>? data, String key, String suffix) {
    final value = data?[key];
    return value is num ? '$value$suffix' : '--';
  }
}

class MetricCard extends StatelessWidget {
  const MetricCard({
    required this.icon,
    required this.label,
    required this.value,
    super.key,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Row(children: [Icon(icon), const SizedBox(width: 8), Text(label)]),
            Text(value, style: Theme.of(context).textTheme.headlineSmall),
          ],
        ),
      ),
    );
  }
}

class PlaceholderPage extends StatelessWidget {
  const PlaceholderPage({
    required this.icon,
    required this.title,
    required this.message,
    super.key,
  });

  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 56),
            const SizedBox(height: 16),
            Text(title, style: Theme.of(context).textTheme.headlineSmall),
            const SizedBox(height: 8),
            Text(message, textAlign: TextAlign.center),
          ],
        ),
      ),
    );
  }
}

class RenderingLabPage extends StatefulWidget {
  const RenderingLabPage({required this.scrollController, super.key});

  final ScrollController scrollController;

  @override
  State<RenderingLabPage> createState() => _RenderingLabPageState();
}

class _RenderingLabPageState extends State<RenderingLabPage>
    with SingleTickerProviderStateMixin {
  late final AnimationController animation;

  @override
  void initState() {
    super.initState();
    animation = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 4),
    )..repeat();
  }

  @override
  void dispose() {
    animation.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) => ListView(
        controller: widget.scrollController,
        padding: const EdgeInsets.all(20),
        children: [
          Text('Flutter 3 渲染实验', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 4),
          Text(
            '窗口 ${constraints.maxWidth.toStringAsFixed(0)} × '
            '${constraints.maxHeight.toStringAsFixed(0)} · 协议 3.0',
          ),
          const SizedBox(height: 16),
          Semantics(
            label: '持续更新的八条折线渲染压力图',
            child: RepaintBoundary(
              child: SizedBox(
                height: 280,
                child: AnimatedBuilder(
                  animation: animation,
                  builder: (_, _) =>
                      CustomPaint(painter: StressChartPainter(animation.value)),
                ),
              ),
            ),
          ),
          const SizedBox(height: 12),
          for (var index = 0; index < 24; index++)
            Card(
              child: ListTile(
                leading: CircleAvatar(child: Text('${index + 1}')),
                title: Text('滚动与合成测试项 ${index + 1}'),
                subtitle: LinearProgressIndicator(
                  value: ((index * 37) % 100) / 100,
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class StressChartPainter extends CustomPainter {
  const StressChartPainter(this.phase);

  final double phase;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(
      Offset.zero & size,
      Paint()..color = const Color(0xff111318),
    );
    final grid = Paint()
      ..color = Colors.white12
      ..strokeWidth = 1;
    for (var index = 1; index < 8; index++) {
      final y = size.height * index / 8;
      canvas.drawLine(Offset(0, y), Offset(size.width, y), grid);
    }
    for (var series = 0; series < 8; series++) {
      final path = Path();
      for (var sample = 0; sample < 320; sample++) {
        final x = size.width * sample / 319;
        final wave = math.sin(sample * 0.055 + phase * math.pi * 2 + series);
        final ripple = math.cos(sample * 0.017 - phase * math.pi * 4 + series);
        final y = size.height * (0.5 + wave * 0.28 + ripple * 0.08);
        if (sample == 0) {
          path.moveTo(x, y);
        } else {
          path.lineTo(x, y);
        }
      }
      canvas.drawPath(
        path,
        Paint()
          ..color = HSVColor.fromAHSV(0.85, series * 45, 0.7, 1).toColor()
          ..strokeWidth = 1.5
          ..style = PaintingStyle.stroke,
      );
    }
  }

  @override
  bool shouldRepaint(StressChartPainter oldDelegate) =>
      phase != oldDelegate.phase;
}
