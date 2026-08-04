import 'dart:async';

import 'package:flutter/material.dart';

class SmoothScrollController extends ScrollController {
  @override
  ScrollPosition createScrollPosition(
    ScrollPhysics physics,
    ScrollContext context,
    ScrollPosition? oldPosition,
  ) => _SmoothScrollPosition(
    physics: physics,
    context: context,
    initialPixels: initialScrollOffset,
    keepScrollOffset: keepScrollOffset,
    oldPosition: oldPosition,
    debugLabel: debugLabel,
  );
}

class _SmoothScrollPosition extends ScrollPositionWithSingleContext {
  _SmoothScrollPosition({
    required super.physics,
    required super.context,
    super.initialPixels,
    super.keepScrollOffset,
    super.oldPosition,
    super.debugLabel,
  });

  double? _wheelTarget;

  @override
  void pointerScroll(double delta) {
    if (delta == 0) return;
    final target = ((_wheelTarget ?? pixels) + delta)
        .clamp(minScrollExtent, maxScrollExtent)
        .toDouble();
    _wheelTarget = target;
    if (target == pixels) {
      goIdle();
      _wheelTarget = null;
      return;
    }
    unawaited(
      animateTo(
        target,
        duration: const Duration(milliseconds: 160),
        curve: Curves.easeOutCubic,
      ).whenComplete(() {
        if (_wheelTarget == target) _wheelTarget = null;
      }),
    );
  }
}
