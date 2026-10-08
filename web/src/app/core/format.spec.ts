import { formatCell, formatterCount, fullPrecision, isNumeric, tone } from './format';

describe('format', () => {
  it('formats each catalog kind with its precision', () => {
    expect(formatCell('Money', 1250000.256)).toBe('1,250,000');
    expect(formatCell('Price', 99.12345)).toBe('99.123');
    expect(formatCell('Bp', 212.6)).toBe('213');
    expect(formatCell('Pct', 0.05234)).toBe('5.23%');
    expect(formatCell('Ratio', 4.567)).toBe('4.57');
    expect(formatCell('Count', 2021)).toBe('2021');
    expect(formatCell('Key', 12345)).toBe('12345');
    expect(formatCell('Text', 'CLO 2024-3')).toBe('CLO 2024-3');
    expect(formatCell('Date', '2026-11-25')).toBe('2026-11-25');
    expect(formatCell('Flag', true)).toBe('✓');
    expect(formatCell('Flag', false)).toBe('');
  });

  it('shows negatives with a minus sign or parentheses', () => {
    expect(formatCell('Money', -1500.4)).toBe('-1,500');
    expect(formatCell('Money', -1500.4, 'parens')).toBe('(1,500)');
    expect(formatCell('Pct', -0.012, 'parens')).toBe('(1.20%)');
  });

  it('never shows a sign on a value that rounds to zero', () => {
    expect(formatCell('Price', -0.0004)).toBe('0.000');
    expect(formatCell('Money', -0.4, 'parens')).toBe('0');
  });

  it('shows nothing for null, empty and non-finite values (never NaN)', () => {
    expect(formatCell('Money', null)).toBe('');
    expect(formatCell('Money', '')).toBe('');
    expect(formatCell('Price', Number.NaN)).toBe('');
    expect(formatCell('Price', 'abc')).toBe('');
    expect(formatCell('Money', '1250.5')).toBe('1,251');
  });

  it('reuses one formatter per kind', () => {
    for (let i = 0; i < 1000; i++) formatCell('Price', i);
    const before = formatterCount();
    for (let i = 0; i < 1000; i++) formatCell('Price', -i);
    expect(formatterCount()).toBe(before);
  });

  it('gives full precision for tooltips', () => {
    expect(fullPrecision('Price', 99.123456789)).toBe('99.123456789');
    expect(fullPrecision('Pct', 0.05)).toBe('5%');
    expect(fullPrecision('Text', null)).toBe('');
  });

  it('knows numeric kinds and up/down tone', () => {
    expect(isNumeric('Money')).toBe(true);
    expect(isNumeric('Text')).toBe(false);
    expect(tone(3)).toBe('up');
    expect(tone(-3)).toBe('down');
    expect(tone(0)).toBeNull();
    expect(tone('x')).toBeNull();
    expect(tone(Number.NaN)).toBeNull();
  });
});
