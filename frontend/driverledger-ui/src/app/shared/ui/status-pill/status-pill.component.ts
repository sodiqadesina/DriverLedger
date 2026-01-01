import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'dl-status-pill',
  standalone: true,
  imports: [CommonModule],
  template: `
    <span class="px-2 py-1 text-xs rounded-full border"
      [ngClass]="klass">
      {{ label }}
    </span>
  `
})
export class StatusPillComponent {
  @Input({ required: true }) label!: string;

  get klass(): string {
    const v = this.label.toLowerCase();
    if (v.includes('hold')) return 'border-yellow-400';
    if (v.includes('ready') || v.includes('posted')) return 'border-green-400';
    if (v.includes('fail')) return 'border-red-400';
    return 'border-gray-400';
  }
}
