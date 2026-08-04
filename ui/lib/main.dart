import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'ipc_probe.dart';

void main() => runApp(const ThrmExperiment());

class ThrmExperiment extends StatelessWidget {
  const ThrmExperiment({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xff6750a4)),
        useMaterial3: true,
      ),
      home: const RenderingStressPage(),
    );
  }
}

class RenderingStressPage extends StatefulWidget {
  const RenderingStressPage({super.key});

  @override
  State<RenderingStressPage> createState() => _RenderingStressPageState();
}

class _RenderingStressPageState extends State<RenderingStressPage>
    with SingleTickerProviderStateMixin {
  late final AnimationController _animation;
  bool _probing = false;
  String _probeStatus = '尚未运行（只发送 Ping / GetConfig）';

  @override
  void initState() {
    super.initState();
    _animation = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 4),
    )..repeat();
  }

  @override
  void dispose() {
    _animation.dispose();
    super.dispose();
  }

  Future<void> _runProbe() async {
    setState(() {
      _probing = true;
      _probeStatus = '正在连接 THRM Core…';
    });
    try {
      final result = await runIpcProbe();
      if (!mounted) return;
      setState(() {
        _probeStatus =
            '通过 · 配置 ${result.configBytes} bytes · 事件 ${result.eventType}';
      });
    } catch (error) {
      if (!mounted) return;
      setState(() => _probeStatus = '失败 · $error');
    } finally {
      if (mounted) setState(() => _probing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('THRM · Flutter 3 可行性实验')),
      body: LayoutBuilder(
        builder: (context, constraints) {
          return CustomScrollView(
            slivers: [
              SliverToBoxAdapter(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '窗口 ${constraints.maxWidth.toStringAsFixed(0)} × '
                        '${constraints.maxHeight.toStringAsFixed(0)}',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 8),
                      const Text('拖动、缩放、最大化窗口并滚动下方列表；图表会持续重绘。'),
                      const SizedBox(height: 16),
                      Semantics(
                        label: '持续更新的八条折线渲染压力图',
                        child: RepaintBoundary(
                          child: SizedBox(
                            height: 280,
                            width: double.infinity,
                            child: AnimatedBuilder(
                              animation: _animation,
                              builder: (_, _) => CustomPaint(
                                painter: StressChartPainter(_animation.value),
                              ),
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                      Wrap(
                        spacing: 12,
                        runSpacing: 8,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: [
                          FilledButton.icon(
                            onPressed: _probing ? null : _runProbe,
                            icon: _probing
                                ? const SizedBox.square(
                                    dimension: 16,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.cable),
                            label: const Text('运行 IPC 探针'),
                          ),
                          Text(_probeStatus),
                        ],
                      ),
                      const SizedBox(height: 12),
                      const Divider(),
                    ],
                  ),
                ),
              ),
              SliverList.builder(
                itemCount: 30,
                itemBuilder: (context, index) => Card(
                  margin: const EdgeInsets.fromLTRB(16, 4, 16, 4),
                  child: ListTile(
                    leading: CircleAvatar(child: Text('${index + 1}')),
                    title: Text('滚动与合成测试项 ${index + 1}'),
                    subtitle: LinearProgressIndicator(
                      value: ((index * 37) % 100) / 100,
                    ),
                    trailing: const Icon(Icons.drag_handle),
                  ),
                ),
              ),
              const SliverToBoxAdapter(child: SizedBox(height: 16)),
            ],
          );
        },
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
    for (var i = 1; i < 8; i++) {
      final y = size.height * i / 8;
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
