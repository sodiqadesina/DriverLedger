import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'dl-nav-badge',
  standalone: true,
  imports: [CommonModule],
  template: `
    <span *ngIf="text"
          class="text-[11px] px-2 py-0.5 rounded-full border bg-slate-50 text-slate-600">
      {{ text }}
    </span>

    <span *ngIf="count !== undefined"
          class="text-[11px] min-w-[22px] h-[22px] inline-flex items-center justify-center rounded-full bg-amber-500 text-white">
      {{ count }}
    </span>
  `
})
export class NavBadgeComponent {
  @Input() text?: string;
  @Input() count?: number;
}
