import * as assert from 'assert';
import { CMD, CONFIG, DEFAULTS, VIEW } from '../../src/constants';

suite('Canvas Constants Test Suite', () => {
  test('CMD.SHOW_CANVAS follows naming convention', () => {
    assert.ok(CMD.SHOW_CANVAS.startsWith('serialtree.'), 'SHOW_CANVAS should start with "serialtree."');
    assert.strictEqual(CMD.SHOW_CANVAS, 'serialtree.showCanvas');
  });

  test('VIEW.CANVAS follows naming convention', () => {
    assert.ok(VIEW.CANVAS.startsWith('serialtree.'), 'CANVAS view should start with "serialtree."');
    assert.strictEqual(VIEW.CANVAS, 'serialtree.canvas');
  });

  test('Canvas config keys exist', () => {
    assert.strictEqual(CONFIG.CANVAS_LAYOUT, 'canvasLayout');
    assert.strictEqual(CONFIG.CANVAS_SHOW_MINIMAP, 'canvasShowMinimap');
    assert.strictEqual(CONFIG.CANVAS_MAX_NODES, 'canvasMaxNodes');
  });

  test('Canvas defaults have expected values', () => {
    assert.strictEqual(DEFAULTS.CANVAS_LAYOUT, 'grid');
    assert.strictEqual(DEFAULTS.CANVAS_SHOW_MINIMAP, true);
    assert.strictEqual(DEFAULTS.CANVAS_MAX_NODES, 500);
  });

  test('Canvas defaults are valid types', () => {
    const validLayouts = ['dagre', 'force', 'grid', 'radial'];
    assert.ok(
      validLayouts.includes(DEFAULTS.CANVAS_LAYOUT),
      `Canvas layout default "${DEFAULTS.CANVAS_LAYOUT}" should be one of: ${validLayouts.join(', ')}`,
    );
    assert.strictEqual(typeof DEFAULTS.CANVAS_SHOW_MINIMAP, 'boolean');
    assert.strictEqual(typeof DEFAULTS.CANVAS_MAX_NODES, 'number');
    assert.ok(DEFAULTS.CANVAS_MAX_NODES > 0, 'Max nodes should be positive');
  });

  test('All CMD values are unique (including canvas)', () => {
    const values = Object.values(CMD);
    const unique = new Set(values);
    assert.strictEqual(values.length, unique.size, 'Command IDs must be unique');
  });

  test('All CONFIG values are unique (including canvas)', () => {
    const values = Object.values(CONFIG);
    const unique = new Set(values);
    assert.strictEqual(values.length, unique.size, 'Config keys must be unique');
  });
});
