import { TestBed } from '@angular/core/testing';
import { Placeholder } from './placeholder';

describe('Placeholder', () => {
  it('says which phase brings the page', async () => {
    TestBed.configureTestingModule({ imports: [Placeholder] });
    const fixture = TestBed.createComponent(Placeholder);
    fixture.componentRef.setInput('title', 'Deal Explorer');
    fixture.componentRef.setInput('phase', 'phase 7');
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Deal Explorer');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Arrives in phase 7');
  });
});
