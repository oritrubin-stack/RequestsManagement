import { BULK_MAX_ITEMS, selectAll, toggleSelection } from './requests-list.models';

describe('bulk selection helpers', () => {
  const ids = (count: number, prefix = 'r') => Array.from({ length: count }, (_, i) => `${prefix}${i}`);

  it('adds and removes a single id without changing the input set', () => {
    const empty = new Set<string>();

    const selected = toggleSelection(empty, 'a', true);
    const cleared = toggleSelection(selected, 'a', false);

    expect([...selected]).toEqual(['a']);
    expect(cleared.size).toBe(0);
    expect(empty.size).toBe(0);
  });

  it('never selects more than the API limit of 100', () => {
    expect(BULK_MAX_ITEMS).toBe(100);

    const full = selectAll(new Set<string>(), ids(150));
    expect(full.size).toBe(100);

    const stillFull = toggleSelection(full, 'extra', true);
    expect(stillFull.size).toBe(100);
    expect(stillFull.has('extra')).toBeFalse();
  });

  it('fills only the remaining capacity when selecting all', () => {
    const partial = selectAll(new Set<string>(), ids(95, 'a'));

    const filled = selectAll(partial, ids(20, 'b'));

    expect(filled.size).toBe(100);
    expect(filled.has('b4')).toBeTrue();
    expect(filled.has('b5')).toBeFalse();
  });
});
