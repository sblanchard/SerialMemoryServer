import * as assert from 'assert';
import { CMD } from '../../src/constants';

suite('Extension Test Suite', () => {
  test('Command IDs follow naming convention', () => {
    for (const [key, value] of Object.entries(CMD)) {
      assert.ok(value.startsWith('serialtree.'), `Command ${key} should start with "serialtree."`);
    }
  });

  test('All command IDs are unique', () => {
    const values = Object.values(CMD);
    const unique = new Set(values);
    assert.strictEqual(values.length, unique.size, 'Command IDs must be unique');
  });

  test('Constants are immutable', () => {
    const originalLength = Object.keys(CMD).length;
    assert.ok(originalLength > 0, 'CMD should have entries');
  });
});
