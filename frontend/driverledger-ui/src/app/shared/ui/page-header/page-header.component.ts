import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'dl-page-header',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="flex items-center justify-between gap-3 py-3">
      <div>
        <h1 class="text-lg font-semibold">{{ title }}</h1>
        <p class="text-sm opacity-70" *ngIf="subtitle">{{ subtitle }}</p>
      </div>
      <ng-content />
    </div>
  `,
})
export class PageHeaderComponent {
  @Input({ required: true }) title!: string;
  @Input() subtitle?: string;
}
