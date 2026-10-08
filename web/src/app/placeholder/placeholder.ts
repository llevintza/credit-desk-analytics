import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Pages that arrive in later phases (README §15) show what's coming instead of a blank screen. */
@Component({
  selector: 'app-placeholder',
  template: `<section class="placeholder" role="status"><h2>{{ title() }}</h2><p>Arrives in {{ phase() }}.</p></section>`,
  styles: `.placeholder { padding: 24px; color: var(--muted); } h2 { color: var(--text); font-size: 16px; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Placeholder {
  readonly title = input.required<string>();
  readonly phase = input.required<string>();
}
