import { formatInsight, insightTooltip, isNegative, isNumericFormat } from './insight-format';

describe('insight cell formatting (README §6 P3, §9.1)', () => {
  it('compacts money for a narrow tile', () => {
    expect(formatInsight('money0', 1_234_567_890)).toBe('1.2B');
    expect(formatInsight('money0', 345_600_000)).toBe('345.6M');
    expect(formatInsight('money0', 950)).toBe('950');
  });

  it('shows ratios as percentages with the format\'s decimals', () => {
    expect(formatInsight('pct0', 0.734)).toBe('73%');
    expect(formatInsight('pct1', 0.0456)).toBe('4.6%');
    expect(formatInsight('pct2', 0.01234)).toBe('1.23%');
  });

  it('formats bp, counts and plain numbers', () => {
    expect(formatInsight('bp1', 12.345)).toBe('12.3');
    expect(formatInsight('int', 12345)).toBe('12,345');
    expect(formatInsight('num0', 2950.4)).toBe('2,950');
    expect(formatInsight('num2', 0.5)).toBe('0.50');
    expect(formatInsight('unknown', 0.5)).toBe('0.50'); // an unknown format falls back to two decimals
  });

  it('honours the negative style and never signs a value shown as zero', () => {
    expect(formatInsight('bp1', -4.25)).toBe('-4.3');
    expect(formatInsight('bp1', -4.25, 'parens')).toBe('(4.3)');
    expect(formatInsight('money0', -2_500_000, 'parens')).toBe('(2.5M)');
    expect(formatInsight('bp1', -0.01)).toBe('0.0');
    expect(formatInsight('pct1', -0.00001)).toBe('0.0%');
  });

  it('shows nothing for null and non-finite numbers, and text as is', () => {
    expect(formatInsight('money0', null)).toBe('');
    expect(formatInsight('pct1', Number.NaN)).toBe('');
    expect(formatInsight('pct1', Number.POSITIVE_INFINITY)).toBe('');
    expect(formatInsight('text', 'CLO')).toBe('CLO');
    expect(formatInsight('money0', 'n/a')).toBe('n/a');
    expect(formatInsight(undefined, 3)).toBe('3');
  });

  it('gives full precision in the tooltip', () => {
    expect(insightTooltip('pct1', 0.07)).toBe('7%');
    expect(insightTooltip('money0', 1234567.89)).toBe('1234567.89');
    expect(insightTooltip('text', 'CLO')).toBe('CLO');
    expect(insightTooltip('money0', null)).toBe('');
  });

  it('knows numeric formats and negative values', () => {
    expect(isNumericFormat('money0')).toBe(true);
    expect(isNumericFormat('text')).toBe(false);
    expect(isNumericFormat(undefined)).toBe(false);
    expect(isNegative(-1)).toBe(true);
    expect(isNegative(0)).toBe(false);
    expect(isNegative('-1')).toBe(false);
    expect(isNegative(null)).toBe(false);
  });
});
