import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'dl-drawer',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div *ngIf="open" class="fixed inset-0 z-50">
      <button class="absolute inset-0 bg-black/40" (click)="close.emit()"></button>

      <aside class="absolute right-0 top-0 h-full w-[85%] max-w-sm bg-white shadow-xl p-4">
        <div class="flex items-center justify-between">
          <div class="font-semibold">{{ title }}</div>
          <button class="text-sm underline" (click)="close.emit()">Close</button>
        </div>

        <div class="mt-4">
          <ng-content />
        </div>
      </aside>
    </div>
  `
})
export class DrawerComponent {
  @Input() open = false;
  @Input() title = '';
  @Output() close = new EventEmitter<void>();
}
