import { DOCUMENT, Injectable, effect, inject, signal } from '@angular/core';
import { NegativeStyle } from './format';

export type Theme = 'dark' | 'light';
export type Palette = 'standard' | 'colorblind';

interface Settings { theme: Theme; palette: Palette; negatives: NegativeStyle; }

const key = 'desk.settings';
const defaults: Settings = { theme: 'dark', palette: 'standard', negatives: 'minus' };

/**
 * Display settings (README §9.1): dark-first theme with a light toggle, a colorblind-safe up/down palette and the
 * negative-number style. Persisted per browser; applied as attributes on <html> so plain CSS variables (and the
 * AG Grid theme built on them) follow without any component re-rendering.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly doc = inject(DOCUMENT);
  private readonly saved = ThemeService.load();

  readonly theme = signal<Theme>(this.saved.theme);
  readonly palette = signal<Palette>(this.saved.palette);
  readonly negatives = signal<NegativeStyle>(this.saved.negatives);

  constructor() {
    effect(() => {
      const root = this.doc.documentElement;
      root.dataset['theme'] = this.theme();
      root.dataset['palette'] = this.palette();
      ThemeService.store({ theme: this.theme(), palette: this.palette(), negatives: this.negatives() });
    });
  }

  toggleTheme(): void {
    this.theme.update((t) => (t === 'dark' ? 'light' : 'dark'));
  }

  // Storage can be unavailable (private mode, blocked site data): settings then live for the session only.
  private static load(): Settings {
    try {
      const raw = localStorage.getItem(key);
      return raw ? { ...defaults, ...(JSON.parse(raw) as Partial<Settings>) } : defaults;
    } catch {
      return defaults;
    }
  }

  private static store(s: Settings): void {
    try {
      localStorage.setItem(key, JSON.stringify(s));
    } catch {
      /* not persisted */
    }
  }
}
